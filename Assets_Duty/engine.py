#!/usr/bin/env python3
# -*- coding: utf-8 -*-

from __future__ import annotations

import traceback

from execution_profiles import build_execution_plan, resolve_execution_profile
from llm_transport import call_llm
from multi_agent import run_multi_agent_schedule
from postprocess import (
    dedupe_pool_by_date,
    merge_schedule_pool,
    normalize_multi_area_schedule_ids,
    reconcile_credit_list,
    recover_missing_debts,
    restore_schedule,
    validate_llm_schedule_entries,
)
from single_pass_executor import run_single_pass_schedule
from state_ops import (
    Context,
    acquire_state_file_lock,
    anonymize_instruction,
    get_pool_entries_with_date,
    load_api_key_from_env,
    load_config,
    load_roster,
    load_state,
    normalize_area_names,
    release_state_file_lock,
    save_json_atomic,
)

__all__ = [
    "run_schedule",
    "merge_schedule_pool",
    "reconcile_credit_list",
    "call_llm",
    "anonymize_instruction",
    "save_json_atomic",
    "dedupe_pool_by_date",
    "merge_input_config",
    "normalize_area_names",
    "normalize_multi_area_schedule_ids",
    "restore_schedule",
    "validate_llm_schedule_entries",
    "recover_missing_debts",
    "get_pool_entries_with_date",
    "load_api_key_from_env",
    "load_config",
    "load_roster",
    "load_state",
]


def merge_input_config(input_data: dict | None) -> dict:
    source = dict(input_data or {})
    nested = source.get("config")
    if isinstance(nested, dict):
        merged = dict(nested)
        merged.update({key: value for key, value in source.items() if key != "config"})
        return merged
    return source


def run_schedule(ctx: Context, input_data: dict, emit_progress_fn=None, stop_event=None):
    state_lock_path = ctx.paths["state"].with_suffix(ctx.paths["state"].suffix + ".lock")
    state_lock_acquired = False
    payload = merge_input_config(input_data)
    try:
        if stop_event and stop_event.is_set():
            raise InterruptedError("Cancelled.")

        acquire_state_file_lock(state_lock_path)
        state_lock_acquired = True

        config = load_config(ctx)
        execution_profile = resolve_execution_profile(payload, config)
        execution_plan = build_execution_plan(execution_profile)

        if execution_plan.runtime_mode.startswith("multi_agent"):
            result = run_multi_agent_schedule(ctx, payload, execution_plan, emit_progress_fn, stop_event)
        else:
            result = run_single_pass_schedule(ctx, payload, execution_plan, emit_progress_fn, stop_event)

        result.setdefault("execution_plan", execution_plan.to_metadata())
        return result
    except Exception as ex:
        traceback.print_exc()
        _logger = getattr(ctx, "logger", None)
        if _logger is not None:
            _logger.error(
                "Engine",
                "run_schedule failed.",
                trace_id=getattr(ctx, "trace_id", ""),
                request_source=getattr(ctx, "request_source", ""),
                exc=ex,
                runtime_mode=execution_plan.runtime_mode if "execution_plan" in locals() else "",
            )
        return {
            "status": "error",
            "message": str(ex),
            "trace_id": str(payload.get("trace_id", "")).strip() if isinstance(payload, dict) else "",
        }
    finally:
        if state_lock_acquired:
            release_state_file_lock(state_lock_path)
