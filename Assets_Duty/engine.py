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
    anonymize_instruction,
    get_pool_entries_with_date,
    load_api_key_from_env,
    load_config,
    load_roster,
    load_state,
    normalize_area_names,
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


def run_schedule(ctx: Context, input_data: dict, emit_progress_fn=None, stop_event=None):
    payload = dict(input_data or {})
    try:
        if stop_event and stop_event.is_set():
            raise InterruptedError("Cancelled.")

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
