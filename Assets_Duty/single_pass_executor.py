from __future__ import annotations

import json
from datetime import datetime
from typing import Any, Dict

from execution_profiles import ExecutionPlan
from llm_transport import call_llm_raw, parse_schedule_completion
from postprocess import (
    estimate_pointer_progress,
    merge_schedule_pool,
    normalize_multi_area_schedule_ids,
    reconcile_credit_list,
    recover_missing_debts,
    restore_schedule,
    validate_llm_schedule_entries,
)
from prompt_gateway import build_single_pass_prompt_messages
from state_ops import (
    Context,
    anonymize_instruction,
    clone_count_map,
    load_api_key_from_env,
    load_config,
    load_roster,
    load_state,
    resolve_debt_credit_conflicts,
    update_state,
)

AI_RESPONSE_MAX_CHARS = 20000


def _resolve_transport_overrides(plan: ExecutionPlan) -> Dict[str, Any] | None:
    if plan.profile.single_pass_strategy in {"edge_tuned", "edge_generic"}:
        return {"temperature": 0.0}
    return None


def build_single_pass_request(
    ctx: Context,
    input_data: dict,
    execution_plan: ExecutionPlan,
    emit_progress_fn=None,
) -> tuple[list[dict], dict]:
    """Build the single_pass prompt (messages) plus a small, serializable resume
    context, without calling any model.

    ``resume_context`` deliberately carries no roster/state so that the settle
    half (:func:`apply_single_pass_completion`) re-reads roster/state at apply
    time, which keeps the external-AI delegation flow stateless and avoids lost
    updates. It is a JSON-serializable dict.
    """
    run_now = datetime.now()
    ctx.config = load_config(ctx)
    name_to_id, id_to_name, all_ids, id_to_active = load_roster(ctx.paths["roster"])
    state_data = load_state(ctx.paths["state"])
    input_data = dict(input_data or {})
    instruction = str(input_data.get("instruction", "Generate duty schedule")).strip()
    trace_id = str(input_data.get("trace_id", "")).strip()

    api_key = (
        str(ctx.config.get("api_key", "")).strip()
        or load_api_key_from_env()
    )
    ctx.config["api_key"] = api_key
    ctx.config["llm_stream"] = True

    area_names: list[str] = []
    area_per_day_counts: dict[str, int] = {}
    if emit_progress_fn:
        emit_progress_fn(
            "planning",
            f"Execution plan resolved: {execution_plan.prompt_pack_strategy} / {execution_plan.runtime_mode}",
            json.dumps(execution_plan.to_metadata(), ensure_ascii=False),
        )

    debt_counts = clone_count_map(state_data.get("debt_counts", {}), set(all_ids))
    credit_counts = clone_count_map(state_data.get("credit_counts", {}), set(all_ids))
    debt_counts, credit_counts = resolve_debt_credit_conflicts(debt_counts, credit_counts)

    start_date = run_now.date()

    messages, prompt_metadata = build_single_pass_prompt_messages(
        execution_plan,
        all_ids=all_ids,
        id_to_active=id_to_active,
        current_time=run_now.strftime("%Y-%m-%d %H:%M"),
        instruction=anonymize_instruction(instruction, name_to_id),
        duty_rule=anonymize_instruction(str(ctx.config.get("duty_rule", "")), name_to_id),
        area_names=area_names,
        area_per_day_counts=area_per_day_counts,
        debt_counts=debt_counts,
        credit_counts=credit_counts,
        start_date=start_date.isoformat(),
        previous_context="",
        last_pointer=int(state_data.get("last_pointer", 0) or 0),
    )

    if emit_progress_fn:
        emit_progress_fn(
            "prompt_ready",
            f"Prompt gateway prepared {prompt_metadata['logical_task_count']} logical tasks.",
            json.dumps(prompt_metadata, ensure_ascii=False),
        )

    transport_overrides = _resolve_transport_overrides(execution_plan)

    resume_context = {
        "mode": execution_plan.runtime_mode,
        "start_date": start_date.isoformat(),
        "trace_id": trace_id,
        "prompt_metadata": prompt_metadata,
        "execution_plan_meta": execution_plan.to_metadata(),
        "single_pass_strategy": execution_plan.profile.single_pass_strategy,
        "transport_overrides": transport_overrides or {},
    }
    return messages, resume_context


def apply_single_pass_completion(
    ctx: Context,
    completion_text: str,
    resume_context: dict,
    stop_event=None,
) -> dict:
    """Parse an already-produced completion (V2 INI / CSV fallback) and settle it
    into persisted state.

    ``completion_text`` may originate from the provider transport
    (:func:`run_single_pass_schedule`) or from an external AI via the CLI
    plan-ingest flow. Roster/state are re-read here so the settle is atomic and
    stateless with respect to ``resume_context``.
    """
    resume_context = dict(resume_context or {})
    trace_id = str(resume_context.get("trace_id", "")).strip()
    start_date_value = resume_context.get("start_date")
    prompt_metadata = resume_context.get("prompt_metadata") or {}
    execution_plan_meta = resume_context.get("execution_plan_meta") or {}
    runtime_mode = resume_context.get("mode") or "single_pass"
    single_pass_strategy = resume_context.get("single_pass_strategy")
    transport_overrides = resume_context.get("transport_overrides") or {}

    name_to_id, id_to_name, all_ids, id_to_active = load_roster(ctx.paths["roster"])
    state_data = load_state(ctx.paths["state"])

    area_names: list[str] = []
    area_per_day_counts: dict[str, int] = {}

    debt_counts = clone_count_map(state_data.get("debt_counts", {}), set(all_ids))
    credit_counts = clone_count_map(state_data.get("credit_counts", {}), set(all_ids))
    debt_counts, credit_counts = resolve_debt_credit_conflicts(debt_counts, credit_counts)

    llm_result, llm_text = parse_schedule_completion(completion_text, start_date_value)
    validate_llm_schedule_entries(llm_result.get("schedule", []))

    normalized_ids = normalize_multi_area_schedule_ids(
        llm_result.get("schedule", []),
        all_ids,
        area_names,
        area_per_day_counts,
    )
    restored = restore_schedule(normalized_ids, id_to_name, area_names, {})
    if not restored:
        raise ValueError("No valid schedule entries.")

    state_delta = dict(llm_result.get("state_delta") or {})
    llm_pointer_after = state_delta.get("pointer_after")
    llm_consumed_credit_ids = state_delta.get("consumed_credit_ids")

    if llm_pointer_after is not None:
        safe_pointer = max(0, min(int(llm_pointer_after), len(all_ids) - 1)) if all_ids else 0
        pointer_progress = {
            "pointer_after": safe_pointer,
            "consumed_credit_ids": list(llm_consumed_credit_ids or []),
        }
    else:
        pointer_progress = estimate_pointer_progress(
            all_ids,
            [person_id for person_id in all_ids if id_to_active.get(person_id, 1) != 0],
            int(state_data.get("last_pointer", 0) or 0),
            debt_counts,
            credit_counts,
            normalized_ids,
        )

    def _apply_state_update(current_state: dict) -> dict:
        next_state = dict(current_state)
        current_debt_counts = clone_count_map(next_state.get("debt_counts", {}), set(all_ids))
        current_credit_counts = clone_count_map(next_state.get("credit_counts", {}), set(all_ids))

        next_state["debt_counts"] = recover_missing_debts(
            current_debt_counts,
            state_delta.get("debt_counts", {}),
            normalized_ids,
        )
        next_state["credit_counts"] = reconcile_credit_list(
            current_credit_counts,
            state_delta.get("credit_counts", {}),
            normalized_ids,
            set(all_ids),
            next_state["debt_counts"],
            True,
            consumed_credit_ids=pointer_progress.get("consumed_credit_ids", []),
        )
        next_state["debt_counts"], next_state["credit_counts"] = resolve_debt_credit_conflicts(
            next_state["debt_counts"],
            next_state["credit_counts"],
        )
        next_state["last_pointer"] = int(pointer_progress.get("pointer_after", next_state.get("last_pointer", 0)) or 0)
        next_state["schedule_pool"] = merge_schedule_pool(restored)
        return next_state

    if stop_event and stop_event.is_set():
        raise InterruptedError("Cancelled before state update.")

    update_state(ctx.paths["state"], _apply_state_update, stop_event=stop_event)

    return {
        "status": "success",
        "ai_response": llm_text[:AI_RESPONSE_MAX_CHARS],
        "trace_id": trace_id,
        "selected_executor": runtime_mode,
        "execution_plan": execution_plan_meta,
        "prompt_gateway": prompt_metadata,
        "single_pass_strategy": single_pass_strategy,
        "transport_overrides": transport_overrides,
    }


def run_single_pass_schedule(
    ctx: Context,
    input_data: dict,
    execution_plan: ExecutionPlan,
    emit_progress_fn=None,
    stop_event=None,
) -> dict:
    """Provider-backed single_pass run: build prompt, call the configured model,
    then settle. Preserves the original end-to-end behavior; the build/apply
    split simply lets an external AI substitute for the model call.
    """
    messages, resume_context = build_single_pass_request(
        ctx,
        input_data,
        execution_plan,
        emit_progress_fn=emit_progress_fn,
    )

    content = call_llm_raw(
        messages,
        ctx.config,
        emit_progress_fn,
        stop_event,
        resume_context.get("transport_overrides") or None,
    )

    return apply_single_pass_completion(ctx, content, resume_context, stop_event=stop_event)
