from __future__ import annotations

import json
from datetime import datetime, timedelta
from typing import Any, Dict

from execution_profiles import ExecutionPlan
from llm_transport import call_llm
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
    get_pool_entries_with_date,
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


def run_single_pass_schedule(
    ctx: Context,
    input_data: dict,
    execution_plan: ExecutionPlan,
    emit_progress_fn=None,
    stop_event=None,
) -> dict:
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
    apply_mode = str(input_data.get("apply_mode", "append")).lower()

    if emit_progress_fn:
        emit_progress_fn(
            "planning",
            f"Execution plan resolved: {execution_plan.prompt_pack_strategy} / {execution_plan.runtime_mode}",
            json.dumps(execution_plan.to_metadata(), ensure_ascii=False),
        )

    debt_counts = clone_count_map(state_data.get("debt_counts", {}), set(all_ids))
    credit_counts = clone_count_map(state_data.get("credit_counts", {}), set(all_ids))
    debt_counts, credit_counts = resolve_debt_credit_conflicts(debt_counts, credit_counts)

    entries = get_pool_entries_with_date(state_data)
    start_date = (entries[-1][1] + timedelta(days=1)) if apply_mode == "append" and entries else run_now.date()

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

    llm_result, llm_text = call_llm(
        messages,
        ctx.config,
        progress_callback=emit_progress_fn,
        stop_event=stop_event,
        transport_overrides=transport_overrides,
        start_date_value=start_date,
    )
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
        state_delta = dict(llm_result.get("state_delta") or {})

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
        next_state["schedule_pool"] = merge_schedule_pool(next_state, restored, apply_mode, start_date)
        return next_state

    update_state(ctx.paths["state"], _apply_state_update, stop_event=stop_event)

    return {
        "status": "success",
        "ai_response": llm_text[:AI_RESPONSE_MAX_CHARS],
        "trace_id": trace_id,
        "selected_executor": execution_plan.runtime_mode,
        "execution_plan": execution_plan.to_metadata(),
        "prompt_gateway": prompt_metadata,
        "single_pass_strategy": execution_plan.profile.single_pass_strategy,
        "transport_overrides": transport_overrides or {},
    }
