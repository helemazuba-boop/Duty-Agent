from __future__ import annotations

import json
from datetime import datetime, timedelta
from typing import Any, Dict

from execution_profiles import ExecutionPlan
from llm_transport import call_llm
from postprocess import (
    merge_schedule_pool,
    normalize_multi_area_schedule_ids,
    reconcile_credit_list,
    recover_missing_debts,
    restore_schedule,
    validate_llm_schedule_entries,
)
from prompt_gateway import build_single_pass_prompt_messages
from state_ops import (
    DEFAULT_PER_DAY,
    Context,
    anonymize_instruction,
    extract_ids_from_value,
    get_pool_entries_with_date,
    load_api_key_from_env,
    load_config,
    load_roster,
    load_state,
    normalize_area_names,
    normalize_area_per_day_counts,
    save_json_atomic,
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
    if not api_key:
        raise ValueError("Missing config/api_key.")

    ctx.config["api_key"] = api_key
    ctx.config["llm_stream"] = True

    area_names = normalize_area_names([])
    area_per_day_counts = normalize_area_per_day_counts(
        area_names,
        {},
        ctx.config.get("per_day", DEFAULT_PER_DAY),
    )
    apply_mode = str(input_data.get("apply_mode", "append")).lower()

    if emit_progress_fn:
        emit_progress_fn(
            "planning",
            f"Execution plan resolved: {execution_plan.prompt_pack_strategy} / {execution_plan.runtime_mode}",
            json.dumps(execution_plan.to_metadata(), ensure_ascii=False),
        )

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
        debt_list=extract_ids_from_value(state_data.get("debt_list", []), set(all_ids)),
        credit_list=extract_ids_from_value(state_data.get("credit_list", []), set(all_ids)),
        previous_context=str(state_data.get("next_run_note", "")).strip(),
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
    )
    validate_llm_schedule_entries(llm_result.get("schedule", []))

    state_data["next_run_note"] = str(llm_result.get("next_run_note", "")).strip()
    normalized_ids = normalize_multi_area_schedule_ids(
        llm_result.get("schedule", []),
        all_ids,
        area_names,
        area_per_day_counts,
    )
    state_data["debt_list"] = recover_missing_debts(
        extract_ids_from_value(state_data.get("debt_list", []), set(all_ids)),
        extract_ids_from_value(llm_result.get("new_debt_ids", []), set(all_ids)),
        normalized_ids,
    )
    state_data["credit_list"] = reconcile_credit_list(
        extract_ids_from_value(state_data.get("credit_list", []), set(all_ids)),
        extract_ids_from_value(llm_result.get("new_credit_ids", []), set(all_ids)),
        normalized_ids,
        set(all_ids),
        state_data["debt_list"],
        "new_credit_ids" in llm_result,
    )

    restored = restore_schedule(normalized_ids, id_to_name, area_names, {})
    if not restored:
        raise ValueError("No valid schedule entries.")

    state_data["schedule_pool"] = merge_schedule_pool(state_data, restored, apply_mode, start_date)
    save_json_atomic(ctx.paths["state"], state_data)

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
