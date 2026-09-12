from __future__ import annotations

import json
from datetime import datetime, timedelta
from typing import Any, Dict, List

from absence_ops import (
    absences_for_window,
    compose_run_notes,
    day_override_count,
    extract_absentees,
    fill_shortfall,
    merge_absence_entries,
    rotation_order,
    trim_excess,
)
from execution_profiles import ExecutionPlan
from llm_transport import call_llm_raw, parse_schedule_completion
from postprocess import (
    estimate_pointer_progress,
    merge_schedule_pool,
    normalize_multi_area_schedule_ids,
    reconcile_credit_list,
    recover_missing_debts,
    restore_schedule,
    try_parse_iso_date,
    validate_llm_schedule_entries,
)
from prompt_gateway import build_single_pass_prompt_messages
from state_ops import (
    Context,
    DEFAULT_SINGLE_AREA_NAME,
    anonymize_instruction,
    clone_count_map,
    get_configured_area_names,
    get_configured_area_per_day_counts,
    load_api_key_from_env,
    load_config,
    load_roster,
    load_state,
    prune_operational_state,
    resolve_debt_credit_conflicts,
    update_state,
)

AI_RESPONSE_MAX_CHARS = 20000


def _enforce_absence_and_counts(
    normalized_ids: List[dict],
    all_ids: List[int],
    id_to_active: Dict[int, int],
    last_pointer: int,
    area_per_day_counts: Dict[str, int],
    day_overrides: Dict[str, Dict[str, int]],
    debt_counts: Dict[int, int],
    absent_ids: set,
) -> List[str]:
    """Deterministic post-parse repair (D-A): strip absentees, trim excess,
    fill shortfall. Mutates ``normalized_ids`` in place; returns warnings."""
    warnings: List[str] = []
    if not normalized_ids:
        return warnings
    rotation = rotation_order(all_ids, id_to_active, last_pointer)

    for entry in normalized_ids:
        date_text = str(entry.get("date", "") or "")
        area_ids: Dict[str, List[int]] = entry.get("area_ids", {})
        day_ids = {person_id for ids in area_ids.values() for person_id in ids}
        entry_adjustments: List[str] = []

        for area_name in list(area_ids.keys()):
            ids = list(area_ids.get(area_name, []))

            stripped = [person_id for person_id in ids if person_id in absent_ids]
            if stripped:
                ids = [person_id for person_id in ids if person_id not in absent_ids]
                day_ids -= set(stripped)
                warnings.append(
                    f"{date_text}/{area_name}: 剔除缺席人员 "
                    + " ".join(str(person_id) for person_id in stripped)
                )
                entry_adjustments.append(f"剔除缺席{len(stripped)}人")

            override_present = bool((day_overrides or {}).get(date_text, {}).get(area_name))
            if area_name in area_per_day_counts or override_present:
                required: int | None = day_override_count(
                    day_overrides, date_text, area_name,
                    int(area_per_day_counts.get(area_name, 0) or 0),
                )
            else:
                required = None  # dynamic model-declared area: leave count as-is

            if required is not None:
                kept, trimmed = trim_excess(ids, required, debt_counts)
                if trimmed:
                    ids = kept
                    day_ids -= set(trimmed)
                    warnings.append(
                        f"{date_text}/{area_name}: 超额裁剪移除 "
                        + " ".join(str(person_id) for person_id in trimmed)
                    )
                    entry_adjustments.append(f"裁剪{len(trimmed)}人")

                if len(ids) < required:
                    candidates = [
                        person_id
                        for person_id in rotation
                        if person_id not in day_ids and person_id not in absent_ids
                    ]
                    ids, added = fill_shortfall(ids, required, candidates, debt_counts)
                    if added:
                        day_ids.update(added)
                        warnings.append(
                            f"{date_text}/{area_name}: 缺额自动补齐 "
                            + " ".join(str(person_id) for person_id in added)
                        )
                        entry_adjustments.append(f"补齐{len(added)}人")
                    if len(ids) < required:
                        warnings.append(
                            f"{date_text}/{area_name}: 人手不足，需 {required} 人仅排上 {len(ids)} 人"
                        )
                        entry_adjustments.append("人手不足")

            area_ids[area_name] = ids

        if entry_adjustments:
            summary = "自动修正: " + "，".join(entry_adjustments)
            entry["note"] = f"{str(entry.get('note', '') or '').strip()} {summary}".strip()

    return warnings


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

    # Persist leave/absence intent from the raw instruction (names → IDs) BEFORE
    # anonymization, so plan-prompt → plan-ingest delegation and provider runs
    # share the same persisted absence ranges.
    extracted_absences = extract_absentees(
        instruction, name_to_id, today=run_now.date()
    )
    if extracted_absences:
        def _merge_absences(current_state: dict) -> dict:
            current_state["absences"] = merge_absence_entries(
                current_state.get("absences", []),
                extracted_absences,
            )
            return current_state

        state_data = update_state(
            ctx.paths["state"], _merge_absences, stop_event=stop_event
        )
        if emit_progress_fn:
            emit_progress_fn(
                "absence_recorded",
                "已从指令记录请假/缺席: " + ", ".join(
                    f"ID {item['id']} ({item['from']}~{item['to']})"
                    for item in extracted_absences
                ),
                json.dumps({"absences": extracted_absences}, ensure_ascii=False),
            )

    api_key = (
        str(ctx.config.get("api_key", "")).strip()
        or load_api_key_from_env()
    )
    ctx.config["api_key"] = api_key
    ctx.config["llm_stream"] = True

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

    # Configured areas/headcounts now reach the prompt (previously hardcoded
    # empty, leaving the area template to the model's guess); the settle half
    # re-derives the same values from config.
    area_names = get_configured_area_names(ctx.config) or [DEFAULT_SINGLE_AREA_NAME]
    area_per_day_counts = get_configured_area_per_day_counts(ctx.config, area_names)
    default_days = int(ctx.config.get("default_days", 7) or 7)
    window_end = start_date + timedelta(days=max(1, default_days) - 1)
    window_absent_ids = absences_for_window(
        state_data.get("absences", []), start_date, window_end
    )
    day_overrides = dict(state_data.get("day_overrides", {}) or {})
    previous_note = compose_run_notes(state_data, today=run_now.date())
    previous_context = f"Previous run note (carry-over from the last schedule): {previous_note}" if previous_note else ""

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
        previous_context=previous_context,
        last_pointer=int(state_data.get("last_pointer", 0) or 0),
        absent_ids=window_absent_ids,
        day_overrides=day_overrides,
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

    config = load_config(ctx)
    area_names = get_configured_area_names(config) or [DEFAULT_SINGLE_AREA_NAME]
    area_per_day_counts = get_configured_area_per_day_counts(config, area_names)

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

    # Sudden-situation enforcement: persisted absences overlapping the schedule
    # window plus model-declared absent IDs are stripped, then counts are
    # repaired deterministically (D-A: fill shortfall, trim excess).
    absent_ids_set: set = set()
    schedule_dates = [
        parsed
        for parsed in (
            try_parse_iso_date(entry.get("date", "")) for entry in normalized_ids
        )
        if parsed is not None
    ]
    if schedule_dates:
        absent_ids_set.update(
            absences_for_window(
                state_data.get("absences", []),
                min(schedule_dates),
                max(schedule_dates),
            )
        )
    for raw_id in ((llm_result.get("state_delta") or {}).get("absent_ids") or []):
        try:
            absent_ids_set.add(int(raw_id))
        except (TypeError, ValueError):
            continue
    absent_ids_set &= set(all_ids)
    adjustment_warnings = _enforce_absence_and_counts(
        normalized_ids,
        all_ids,
        id_to_active,
        int(state_data.get("last_pointer", 0) or 0),
        area_per_day_counts,
        dict(state_data.get("day_overrides", {}) or {}),
        debt_counts,
        absent_ids_set,
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
        prune_operational_state(next_state)
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
        "adjustment_warnings": adjustment_warnings,
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
