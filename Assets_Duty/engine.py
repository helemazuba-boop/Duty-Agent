#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import json
import traceback
from datetime import datetime, timedelta

from execution_profiles import build_execution_plan, resolve_execution_profile
from llm_transport import call_llm
from postprocess import (
    dedupe_pool_by_date,
    merge_schedule_pool,
    normalize_multi_area_schedule_ids,
    reconcile_credit_list,
    recover_missing_debts,
    restore_schedule,
    validate_llm_schedule_entries,
)
from prompt_gateway import build_schedule_prompt_messages
from state_ops import (
    DEFAULT_PER_DAY,
    Context,
    acquire_state_file_lock,
    anonymize_instruction,
    extract_ids_from_value,
    get_pool_entries_with_date,
    load_api_key_from_env,
    load_config,
    load_roster,
    load_state,
    merge_input_config,
    normalize_area_names,
    normalize_area_per_day_counts,
    parse_bool,
    release_state_file_lock,
    save_json_atomic,
)

AI_RESPONSE_MAX_CHARS = 20000


def run_schedule(ctx: Context, input_data: dict, emit_progress_fn=None, stop_event=None):
    state_lock_path = ctx.paths["state"].with_suffix(ctx.paths["state"].suffix + ".lock")
    state_lock_acquired = False
    try:
        if stop_event and stop_event.is_set():
            raise InterruptedError("Cancelled.")

        run_now = datetime.now()
        ctx.config = load_config(ctx)
        name_to_id, id_to_name, all_ids, id_to_active = load_roster(ctx.paths["roster"])

        acquire_state_file_lock(state_lock_path)
        state_lock_acquired = True

        state_data = load_state(ctx.paths["state"])
        input_data = merge_input_config(input_data)
        instruction = str(input_data.get("instruction", "Generate duty schedule")).strip()
        trace_id = str(input_data.get("trace_id", "")).strip()

        base_url = str(input_data.get("base_url", ctx.config.get("base_url", ""))).strip()
        model = str(input_data.get("model", ctx.config.get("model", ""))).strip()
        api_key = (
            str(input_data.get("api_key", "")).strip()
            or str(ctx.config.get("api_key", "")).strip()
            or load_api_key_from_env()
        )
        if not base_url or not model or not api_key:
            raise ValueError("Missing config/api_key.")

        ctx.config.update(
            {
                "base_url": base_url,
                "model": model,
                "api_key": api_key,
                "model_profile": input_data.get("model_profile", ctx.config.get("model_profile", "auto")),
                "orchestration_mode": input_data.get(
                    "orchestration_mode",
                    ctx.config.get("orchestration_mode", "auto"),
                ),
                "provider_hint": input_data.get("provider_hint", ctx.config.get("provider_hint", "")),
            }
        )
        ctx.config["llm_stream"] = parse_bool(
            input_data.get("llm_stream", ctx.config.get("llm_stream", True)),
            True,
        )

        area_names = normalize_area_names(input_data.get("area_names", ctx.config.get("area_names", [])))
        area_per_day_counts = normalize_area_per_day_counts(
            area_names,
            input_data.get("area_per_day_counts", ctx.config.get("area_per_day_counts", {})),
            input_data.get("per_day", DEFAULT_PER_DAY),
        )
        apply_mode = str(input_data.get("apply_mode", "append")).lower()

        execution_profile = resolve_execution_profile(input_data, ctx.config)
        execution_plan = build_execution_plan(execution_profile)

        if emit_progress_fn:
            emit_progress_fn(
                "planning",
                f"Execution plan resolved: {execution_plan.prompt_pack_strategy} / {execution_plan.runtime_mode}",
                json.dumps(execution_plan.to_metadata(), ensure_ascii=False),
            )

        entries = get_pool_entries_with_date(state_data)
        start_date = (entries[-1][1] + timedelta(days=1)) if apply_mode == "append" and entries else run_now.date()

        messages, prompt_metadata = build_schedule_prompt_messages(
            execution_plan,
            all_ids=all_ids,
            id_to_active=id_to_active,
            current_time=run_now.strftime("%Y-%m-%d %H:%M"),
            instruction=anonymize_instruction(instruction, name_to_id),
            duty_rule=anonymize_instruction(str(input_data.get("duty_rule", "")), name_to_id),
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

        llm_result, llm_text = call_llm(messages, ctx.config, progress_callback=emit_progress_fn, stop_event=stop_event)
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

        restored = restore_schedule(normalized_ids, id_to_name, area_names, input_data.get("existing_notes", {}))
        if not restored:
            raise ValueError("No valid schedule entries.")

        state_data["schedule_pool"] = merge_schedule_pool(state_data, restored, apply_mode, start_date)
        save_json_atomic(ctx.paths["state"], state_data)

        return {
            "status": "success",
            "ai_response": llm_text[:AI_RESPONSE_MAX_CHARS],
            "trace_id": trace_id,
            "execution_plan": execution_plan.to_metadata(),
            "prompt_gateway": prompt_metadata,
        }
    except Exception as ex:
        traceback.print_exc()
        return {
            "status": "error",
            "message": str(ex),
            "trace_id": str(input_data.get("trace_id", "")).strip() if isinstance(input_data, dict) else "",
        }
    finally:
        if state_lock_acquired:
            release_state_file_lock(state_lock_path)


def run_duty_agent(ctx: Context, input_data: dict, emit_progress_fn=None, stop_event=None):
    return run_schedule(ctx, input_data, emit_progress_fn, stop_event)
