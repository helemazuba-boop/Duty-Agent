#!/usr/bin/env python3
# -*- coding: utf-8 -*-

from __future__ import annotations

from execution_profiles import build_execution_plan, resolve_execution_profile
from llm_transport import call_llm
from multi_agent import run_multi_agent_schedule
from offline_scheduler import run_offline_schedule
from orchestrator.executor import run_orchestrator_schedule
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
    sanitize_error_for_client,
)
from tool_loop.executor import run_tool_loop_schedule

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

# FROZEN modes (2026-10-22 audit decision): tool_loop and orchestrator can no
# longer be selected from the config surface (the persisted-config whitelist
# drops `tool_loop_mode` / `orchestration_mode`), and their remaining
# protocol/schema drift is deliberately NOT being fixed for now. The code stays
# in place (unit tests drive the executors directly), but the engine refuses to
# dispatch them so no half-fixed dormant path can produce production schedules.
FROZEN_RUNTIME_MODES = ("tool_loop", "orchestrator")


def run_schedule(ctx: Context, input_data: dict, emit_progress_fn=None, stop_event=None):
    payload = dict(input_data or {})
    try:
        if stop_event and stop_event.is_set():
            raise InterruptedError("Cancelled.")

        config = load_config(ctx)
        execution_profile = resolve_execution_profile(payload, config)
        execution_plan = build_execution_plan(execution_profile)

        if execution_plan.runtime_mode in FROZEN_RUNTIME_MODES:
            _logger = getattr(ctx, "logger", None)
            if _logger is not None:
                _logger.warn(
                    "Engine",
                    "Rejected frozen runtime mode.",
                    trace_id=getattr(ctx, "trace_id", ""),
                    request_source=getattr(ctx, "request_source", ""),
                    runtime_mode=execution_plan.runtime_mode,
                )
            return {
                "status": "error",
                "code": "mode_frozen",
                "message": (
                    f"执行模式 '{execution_plan.runtime_mode}' 已冻结（暂停维护）。"
                    "请在配置中选择 single_pass / multi_agent / offline。"
                ),
                "trace_id": str(payload.get("trace_id", "")).strip() if isinstance(payload, dict) else "",
            }

        if execution_plan.runtime_mode.startswith("multi_agent"):
            result = run_multi_agent_schedule(ctx, payload, execution_plan, emit_progress_fn, stop_event)
        elif execution_plan.runtime_mode == "tool_loop":
            result = run_tool_loop_schedule(ctx, payload, execution_plan, emit_progress_fn, stop_event)
        elif execution_plan.runtime_mode == "orchestrator":
            result = run_orchestrator_schedule(ctx, payload, execution_plan, emit_progress_fn, stop_event)
        elif execution_plan.runtime_mode == "offline":
            result = run_offline_schedule(ctx, payload, execution_plan, emit_progress_fn, stop_event)
        else:
            result = run_single_pass_schedule(ctx, payload, execution_plan, emit_progress_fn, stop_event)

        result.setdefault("execution_plan", execution_plan.to_metadata())
        return result
    except Exception as ex:
        # 不写 stderr：后端 stdout/stderr 是通向宿主客户端的管道，缓冲区写满
        # 会永久阻塞写入线程（若在事件循环上则整个服务假死）。异常已由下方
        # 文件 logger 完整记录（含 traceback）。
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
            "message": sanitize_error_for_client(str(ex)),
            "trace_id": str(payload.get("trace_id", "")).strip() if isinstance(payload, dict) else "",
        }
