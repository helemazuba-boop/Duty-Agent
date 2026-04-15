"""
Orchestrator Agent executor — rounds + time-window multi-agent polling.

Protocol:
  第0步：Orchestrator 初始化 → 判断 total_slots ≥ 7
         ├─ < 7：Orchestrator 自己填充（不走 TimeWindow Agent）
         └─ ≥ 7：decompose_round → 切 N 个时间窗口

  第1步：TimeWindow Agent 并行执行（timeout 60s × 2 次）
         → 各输出 INI 片段

  第2步：Orchestrator 汇总片段 → 送 ini_handler.parse_and_apply

  第3步：
         ├─ finalized → 持久化 → 结束
         └─ 有 hints → 分类 → 重试或 Orchestrator 自己补

  第4步：重复直到终止
"""
from __future__ import annotations

import re
import traceback
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import date, datetime, timedelta
from typing import Any, Callable, Dict, List, Optional, Set, Tuple

from execution_profiles import ExecutionPlan
from llm_transport import call_llm_raw
from state_ops import load_api_key_from_env
from multi_agent.contracts import FrozenSnapshot
from state_ops import (
    Context,
    DEFAULT_SINGLE_AREA_NAME,
    anonymize_instruction,
    clone_count_map,
    load_config,
    load_roster,
    load_state,
    normalize_count_map,
    resolve_debt_credit_conflicts,
    update_state,
)
from tool_loop.ini_handler import (
    ExecutionCtx,
    PollingFlags,
    ScheduleUnit,
    parse_and_apply,
)

from .aggregator import concatenate_ini_fragments
from .context import Hint, OrchestratorContext, TimeWindow
from .decomposer import decompose_round, should_use_timewindow_agents
from .hints_handler import (
    build_retry_context,
    classify_hints,
    determine_retry_strategy,
    parse_hints_from_execution_ctx,
)
from .prompt import (
    build_orchestrator_round_prompt,
    build_orchestrator_system_prompt,
    build_timewindow_retry_prompt,
    build_timewindow_system_prompt,
    fmt_count_map,
)


# ------------------------------------------------------------------------------
# TimeWindow Agent 配置（不暴露工具，纯文本）
# ------------------------------------------------------------------------------

TW_CONFIG = {
    "temperature": 0.1,
    "max_tokens": 2048,
    "timeout": 60,
    "max_retries": 2,
}


# ------------------------------------------------------------------------------
# 公开入口
# ------------------------------------------------------------------------------

def run_orchestrator_schedule(
    ctx: Context,
    input_data: dict,
    execution_plan: ExecutionPlan,
    emit_progress_fn: Optional[Callable[..., None]] = None,
    stop_event: Optional[object] = None,
) -> Dict[str, Any]:
    try:
        if stop_event is not None and getattr(stop_event, "is_set", lambda: False)():
            raise InterruptedError("Cancelled before start.")

        # --------------------------------------------------------------------------
        # Bootstrap
        # --------------------------------------------------------------------------
        (
            snapshot,
            flags,
            config,
            total_dates,
            required_slots,
        ) = _orchestrator_bootstrap(ctx, input_data)

        _emit_progress(emit_progress_fn, "orchestrator_start", "Orchestrator started.", {
            "total_slots": len(required_slots),
            "hints_on": flags.hints_on,
            "max_rounds": flags.max_rounds,
            "all_ids": snapshot.all_ids,
        })

        # --------------------------------------------------------------------------
        # 判断是否分派 TimeWindow Agent
        # --------------------------------------------------------------------------
        orch_ctx = _build_orchestrator_context(snapshot, flags, config, total_dates)
        use_agents = should_use_timewindow_agents(orch_ctx, total_dates)

        _emit_progress(emit_progress_fn, "orchestrator_mode", "Mode selected.", {
            "use_timewindow_agents": use_agents,
            "total_slots": orch_ctx.total_schedule_slots(total_dates),
        })

        if not use_agents:
            return _orchestrator_direct(ctx, orch_ctx, config, total_dates, required_slots,
                                        emit_progress_fn, stop_event)

        # --------------------------------------------------------------------------
        # Polling 主循环
        # --------------------------------------------------------------------------
        return _orchestrator_poll_loop(
            ctx, orch_ctx, config, total_dates, required_slots,
            emit_progress_fn, stop_event,
        )

    except InterruptedError:
        raise
    except Exception as ex:
        tb = traceback.format_exc()
        _logger = getattr(ctx, "logger", None)
        if _logger is not None:
            _logger.error(
                "Orchestrator",
                "run_orchestrator_schedule failed.",
                trace_id=getattr(ctx, "trace_id", ""),
                exc=ex,
            )
        return {
            "status": "error",
            "message": str(ex),
            "trace_id": str(input_data.get("trace_id", "")).strip() or "",
        }


# ------------------------------------------------------------------------------
# Orchestrator 直接模式（< 7 slots，Orchestrator 自己做）
# ------------------------------------------------------------------------------

def _orchestrator_direct(
    ctx: Context,
    orch_ctx: OrchestratorContext,
    config: dict,
    total_dates: List[date],
    required_slots: List[ScheduleUnit],
    emit_progress_fn: Optional[Callable],
    stop_event: Optional[object],
) -> Dict[str, Any]:
    """total_slots < 7，Orchestrator 直接用 INI 循环自己做（无 TimeWindow Agent）."""
    _emit_progress(emit_progress_fn, "orchestrator_direct", "Direct mode (< 7 slots).", {})

    messages: List[dict] = []
    system_prompt = build_orchestrator_system_prompt(orch_ctx, total_dates)
    messages.append({"role": "system", "content": system_prompt})
    messages.append({
        "role": "user",
        "content": f"请为 {total_dates[0].isoformat()} ~ {total_dates[-1].isoformat()} 生成排班。",
    })

    state_snapshot = _get_state_snapshot(orch_ctx, total_dates, required_slots)
    exec_ctx: Optional[ExecutionCtx] = None

    for round_num in range(1, orch_ctx.max_rounds + 1):
        if stop_event is not None and getattr(stop_event, "is_set", lambda: False)():
            raise InterruptedError("Cancelled.")

        _emit_progress(emit_progress_fn, "orchestrator_direct_round",
                       f"Round {round_num}", {"round": round_num})

        raw_content = _llm_call(messages, config, tools=None,
                                 stop_event=stop_event, emit_progress_fn=emit_progress_fn)
        messages.append({"role": "assistant", "content": raw_content})

        ini_text = _extract_ini_text(raw_content)
        if not ini_text:
            messages.append({"role": "user", "content": "未检测到 INI 格式输出，请输出 INI 格式的排班。"})
            continue

        response_ini, exec_ctx = parse_and_apply(
            raw_ini=ini_text,
            all_ids=orch_ctx.all_ids,
            debt_counts=clone_count_map(orch_ctx.state.get("debt_counts", {})),
            credit_counts=clone_count_map(orch_ctx.state.get("credit_counts", {})),
            inactive_ids=orch_ctx.inactive_ids,
            last_pointer=orch_ctx.last_pointer,
            start_date=total_dates[0],
            schedule_pool=[],
            required_slots=required_slots,
            flags=PollingFlags(hints_on=orch_ctx.hints_on, max_rounds=orch_ctx.max_rounds),
            round_num=round_num,
        )

        if exec_ctx.finalized:
            _emit_progress(emit_progress_fn, "orchestrator_direct_finalized", "Finalized.", {})
            break

        hints = parse_hints_from_execution_ctx(exec_ctx)
        messages.append({"role": "user", "content": _build_hint_feedback(exec_ctx, hints)})

    if exec_ctx is None:
        return {"status": "error", "message": "Direct mode ended without result.", "trace_id": orch_ctx.trace_id}

    return _finalize(ctx, exec_ctx, orch_ctx, emit_progress_fn)


# ------------------------------------------------------------------------------
# Orchestrator TimeWindow Agent 轮询模式
# ------------------------------------------------------------------------------

def _orchestrator_poll_loop(
    ctx: Context,
    orch_ctx: OrchestratorContext,
    config: dict,
    total_dates: List[date],
    required_slots: List[ScheduleUnit],
    emit_progress_fn: Optional[Callable],
    stop_event: Optional[object],
) -> Dict[str, Any]:
    """Orchestrator + TimeWindow Agent 轮询主循环."""
    windows = decompose_round(orch_ctx, total_dates)
    _emit_progress(emit_progress_fn, "orchestrator_windows",
                   f"Decomposed into {len(windows)} windows.", {
                       "windows": [{"name": w.name, "dates": len(w.dates)} for w in windows],
                   })

    # Orchestrator messages（包含对话历史）
    messages: List[dict] = []
    system_prompt = build_orchestrator_system_prompt(orch_ctx, total_dates)
    messages.append({"role": "system", "content": system_prompt})
    messages.append({
        "role": "user",
        "content": _build_initial_user_prompt(orch_ctx, total_dates, windows),
    })

    # Orchestrator 第一轮：分解任务 + 分派
    orchestrator_response = _llm_call(messages, config, tools=None,
                                        stop_event=stop_event, emit_progress_fn=emit_progress_fn)
    messages.append({"role": "assistant", "content": orchestrator_response})

    # 提取 Orchestrator 的指令（窗口分配结果）
    window_assignments = _extract_window_assignments(orchestrator_response, windows)

    exec_ctx: Optional[ExecutionCtx] = None

    for round_num in range(1, orch_ctx.max_rounds + 1):
        if stop_event is not None and getattr(stop_event, "is_set", lambda: False)():
            raise InterruptedError("Cancelled.")

        _emit_progress(emit_progress_fn, "orchestrator_round",
                       f"Round {round_num}", {"round": round_num})

        # --------------------------------------------------------------------------
        # 并行执行所有 TimeWindow Agent
        # --------------------------------------------------------------------------
        fragments: List[Tuple[TimeWindow, str]] = []
        with ThreadPoolExecutor(max_workers=len(windows)) as pool:
            futures = {
                pool.submit(
                    _call_timewindow_agent,
                    w,
                    orch_ctx,
                    config,
                    window_assignments.get(w.index, ""),
                    stop_event,
                ): w
                for w in windows
            }
            for future in as_completed(futures):
                window = futures[future]
                try:
                    fragment = future.result(timeout=TW_CONFIG["timeout"] + 10)
                except Exception as ex:
                    _emit_progress(emit_progress_fn, "orchestrator_tw_error",
                                   f"TimeWindow {window.name} failed: {ex}", {})
                    fragment = f"; [[#{window.name}]]\n; ERROR: {ex}"
                fragments.append((window, fragment))

        # --------------------------------------------------------------------------
        # 汇总 INI → ini_handler
        # --------------------------------------------------------------------------
        orch_ctx._all_dates = total_dates  # 临时注入，供 aggregator 使用
        combined_ini = concatenate_ini_fragments(fragments, orch_ctx)

        response_ini, exec_ctx = parse_and_apply(
            raw_ini=combined_ini,
            all_ids=orch_ctx.all_ids,
            debt_counts=clone_count_map(orch_ctx.state.get("debt_counts", {})),
            credit_counts=clone_count_map(orch_ctx.state.get("credit_counts", {})),
            inactive_ids=orch_ctx.inactive_ids,
            last_pointer=orch_ctx.last_pointer,
            start_date=total_dates[0],
            schedule_pool=[],
            required_slots=required_slots,
            flags=PollingFlags(hints_on=orch_ctx.hints_on, max_rounds=orch_ctx.max_rounds),
            round_num=round_num,
        )

        if exec_ctx.finalized:
            _emit_progress(emit_progress_fn, "orchestrator_finalized",
                           "Finalized by ini_handler.", {"round": round_num})
            break

        # --------------------------------------------------------------------------
        # 处理 hints
        # --------------------------------------------------------------------------
        hints = parse_hints_from_execution_ctx(exec_ctx)
        _emit_progress(emit_progress_fn, "orchestrator_hints",
                       f"{len(hints)} hints received.", {
                           "conflicts": sum(1 for h in hints if h.level == "CONFLICT"),
                           "warnings": sum(1 for h in hints if h.level == "WARNING"),
                       })

        strategy = determine_retry_strategy(hints)
        _emit_progress(emit_progress_fn, "orchestrator_retry_strategy",
                       strategy["reason"], {})

        if strategy["action"] == "finalize":
            break

        retry_context = build_retry_context(hints, windows)

        # --------------------------------------------------------------------------
        # 重试对应窗口
        # --------------------------------------------------------------------------
        retry_windows = strategy.get("retry_windows", [])
        for idx in retry_windows:
            window = windows[idx]
            hint_msg = retry_context.get(idx, "")
            retry_prompt = build_timewindow_retry_prompt(window, orch_ctx, hint_msg)
            with ThreadPoolExecutor(max_workers=1) as pool:
                future = pool.submit(
                    _call_timewindow_agent_retry,
                    window, orch_ctx, config, retry_prompt, stop_event,
                )
                try:
                    fragment = future.result(timeout=TW_CONFIG["timeout"] + 10)
                except Exception:
                    fragment = f"; [[#{window.name}]]\n; RETRY FAILED"
                for i, (w, _) in enumerate(fragments):
                    if w.index == idx:
                        fragments[i] = (window, fragment)
                        break

        # --------------------------------------------------------------------------
        # 重新汇总并送解析器
        # --------------------------------------------------------------------------
        combined_ini = concatenate_ini_fragments(fragments, orch_ctx)
        response_ini, exec_ctx = parse_and_apply(
            raw_ini=combined_ini,
            all_ids=orch_ctx.all_ids,
            debt_counts=clone_count_map(orch_ctx.state.get("debt_counts", {})),
            credit_counts=clone_count_map(orch_ctx.state.get("credit_counts", {})),
            inactive_ids=orch_ctx.inactive_ids,
            last_pointer=orch_ctx.last_pointer,
            start_date=total_dates[0],
            schedule_pool=[],
            required_slots=required_slots,
            flags=PollingFlags(hints_on=orch_ctx.hints_on, max_rounds=orch_ctx.max_rounds),
            round_num=round_num,
        )

        if exec_ctx.finalized:
            break

        # --------------------------------------------------------------------------
        # 将 Python 响应和 hints 反馈给 Orchestrator
        # --------------------------------------------------------------------------
        round_prompt = build_orchestrator_round_prompt(
            orch_ctx, total_dates, round_num, response_ini, hints
        )
        messages.append({"role": "user", "content": round_prompt})

        orchestrator_response = _llm_call(messages, config, tools=None,
                                            stop_event=stop_event, emit_progress_fn=emit_progress_fn)
        messages.append({"role": "assistant", "content": orchestrator_response})

        # 更新窗口分配（Orchestrator 可能重分配）
        new_assignments = _extract_window_assignments(orchestrator_response, windows)
        if new_assignments:
            window_assignments.update(new_assignments)

    if exec_ctx is None:
        return {"status": "error", "message": "Poll loop ended without result.", "trace_id": orch_ctx.trace_id}

    return _finalize(ctx, exec_ctx, orch_ctx, emit_progress_fn)


# ------------------------------------------------------------------------------
# Bootstrap 辅助
# ------------------------------------------------------------------------------

def _orchestrator_bootstrap(
    ctx: Context,
    input_data: dict,
) -> Tuple[FrozenSnapshot, PollingFlags, dict, List[date], List[ScheduleUnit]]:
    config = load_config(ctx)
    state_data = load_state(ctx.paths["state"])
    name_to_id, id_to_name, all_ids, id_to_active = load_roster(ctx.paths["roster"])

    inactive_ids = {pid for pid in all_ids if id_to_active.get(pid, 1) == 0}
    debt_list = _expand_debt_counts(state_data.get("debt_counts", {}))
    credit_list = _expand_credit_counts(state_data.get("credit_counts", {}))

    instruction = str(input_data.get("instruction", "生成排班")).strip()
    instruction = anonymize_instruction(instruction, name_to_id)
    request_time = datetime.now()
    start_date = request_time.date()

    polling_cfg: dict = config.get("polling", {}) or {}
    flags = PollingFlags(
        hints_on=bool(polling_cfg.get("hints_on", True)),
        max_rounds=int(polling_cfg.get("max_rounds", 15)),
    )

    snapshot = FrozenSnapshot(
        trace_id=str(input_data.get("trace_id", "")).strip() or "",
        request_source=str(input_data.get("request_source", "")).strip() or "orchestrator",
        instruction=instruction,
        request_time=request_time,
        start_date=start_date,
        config=config,
        state=state_data,
        name_to_id=name_to_id,
        id_to_name=id_to_name,
        all_ids=all_ids,
        active_ids=[pid for pid in all_ids if pid not in inactive_ids],
        inactive_ids=list(inactive_ids),
        id_to_active=id_to_active,
        debt_list=debt_list,
        credit_list=credit_list,
        last_pointer=int(state_data.get("last_pointer", 0) or 0),
        previous_note=str(state_data.get("next_run_note", "")),
        duty_rule=str(config.get("duty_rule", "")).strip(),
    )

    # 计算日期范围
    total_dates = _compute_dates(
        instruction, start_date, config.get("default_days", 7)
    )

    # 计算 required_slots
    area_names = _get_area_names(config)
    required_slots = _build_required_slots(instruction, total_dates, id_to_name, area_names)

    return snapshot, flags, config, total_dates, required_slots


def _build_orchestrator_context(
    snapshot: FrozenSnapshot,
    flags: PollingFlags,
    config: dict,
    total_dates: List[date],
) -> OrchestratorContext:
    area_names = _get_area_names(config)
    area_per_day_counts = _get_area_per_day_counts(config, area_names)

    ctx = OrchestratorContext.from_frozen_snapshot(
        snapshot=snapshot,
        all_areas=area_names,
        area_per_day_counts=area_per_day_counts,
        hints_on=flags.hints_on,
    )
    ctx._all_dates = total_dates
    return ctx


def _get_state_snapshot(orch_ctx: OrchestratorContext, total_dates: List[date],
                         required_slots: List[ScheduleUnit]) -> dict:
    return {
        "all_ids": orch_ctx.all_ids,
        "debt_counts": orch_ctx.state.get("debt_counts", {}),
        "credit_counts": orch_ctx.state.get("credit_counts", {}),
        "inactive_ids": orch_ctx.inactive_ids,
        "last_pointer": orch_ctx.last_pointer,
        "required_slots": required_slots,
    }


# ------------------------------------------------------------------------------
# TimeWindow Agent 调用
# ------------------------------------------------------------------------------

def _call_timewindow_agent(
    window: TimeWindow,
    orch_ctx: OrchestratorContext,
    config: dict,
    window_assignment: str,
    stop_event: Optional[object],
) -> str:
    """并行调用一个 TimeWindow Agent，返回 INI 文本."""
    system_prompt = build_timewindow_system_prompt(window, orch_ctx)
    user_prompt = window_assignment or (
        f"请为 {window.name} ({window.dates[0].isoformat()} ~ {window.dates[-1].isoformat()}) "
        "填充排班。直接输出 INI 片段，不需要 [state]。"
    )
    messages = [
        {"role": "system", "content": system_prompt},
        {"role": "user", "content": user_prompt},
    ]

    for attempt in range(TW_CONFIG["max_retries"] + 1):
        try:
            raw = _llm_call(messages, config, tools=None,
                            stop_event=stop_event, emit_progress_fn=None)
            messages.append({"role": "assistant", "content": raw})
            ini = _extract_ini_text(raw)
            if ini:
                return ini
            if attempt < TW_CONFIG["max_retries"]:
                messages.append({"role": "user", "content": "未检测到 INI 格式，请输出 INI 片段。"})
                continue
        except Exception:
            if attempt < TW_CONFIG["max_retries"]:
                continue

    return f"; [[#{window.name}]]\n; TIMEOUT/ERROR: no output"


def _call_timewindow_agent_retry(
    window: TimeWindow,
    orch_ctx: OrchestratorContext,
    config: dict,
    retry_prompt: str,
    stop_event: Optional[object],
) -> str:
    """TimeWindow Agent 重试."""
    messages = [
        {"role": "system", "content": build_timewindow_system_prompt(window, orch_ctx)},
        {"role": "user", "content": retry_prompt},
    ]
    try:
        raw = _llm_call(messages, config, tools=None,
                        stop_event=stop_event, emit_progress_fn=None)
        ini = _extract_ini_text(raw)
        return ini if ini else f"; [[#{window.name}]]\n; RETRY FAILED: no INI"
    except Exception as ex:
        return f"; [[#{window.name}]]\n; RETRY ERROR: {ex}"


# ------------------------------------------------------------------------------
# INI 文本提取
# ------------------------------------------------------------------------------

def _extract_ini_text(content: str) -> str:
    """从 LLM 输出中提取 INI 文本（去掉 markdown 包装）."""
    content = content.strip()
    # 去掉 ```ini ... ``` 或 ``` ... ```
    fence_m = re.match(r"```(?:ini|text)?\n?(.*?)```", content, re.DOTALL)
    if fence_m:
        return fence_m.group(1).strip()
    # 去掉 <think>...</think>
    content = re.sub(r"<think>.*?</think>", "", content, flags=re.DOTALL)
    return content.strip()


def _extract_window_assignments(content: str, windows: List[TimeWindow]) -> Dict[int, str]:
    """从 Orchestrator 输出中提取各窗口的分配说明."""
    assignments: Dict[int, str] = {}
    lines = content.splitlines()
    current_window: Optional[TimeWindow] = None
    buf: List[str] = []

    for line in lines:
        line = line.strip()
        if not line or line.startswith("#") or line.startswith(";") and not line.startswith("; "):
            continue
        m = re.match(r"\[\[#(.+?)\]\]", line, re.IGNORECASE)
        if m:
            name = m.group(1)
            for w in windows:
                if w.name == name:
                    if current_window and buf:
                        assignments[current_window.index] = "\n".join(buf)
                    current_window = w
                    buf = []
                    break
            continue
        if current_window:
            buf.append(line)

    if current_window and buf:
        assignments[current_window.index] = "\n".join(buf)

    return assignments


# ------------------------------------------------------------------------------
# LLM 调用
# ------------------------------------------------------------------------------

def _llm_call(
    messages: List[dict],
    config: dict,
    tools: Optional[List[dict]],
    stop_event: Optional[object],
    emit_progress_fn: Optional[Callable],
) -> str:
    api_key = str(config.get("api_key", "")).strip() or load_api_key_from_env()
    cfg = dict(config)
    cfg["api_key"] = api_key
    return call_llm_raw(
        messages=messages,
        config=cfg,
        progress_callback=emit_progress_fn,
        stop_event=stop_event,
        tools=tools,
    )


# ------------------------------------------------------------------------------
# 反馈与最终化
# ------------------------------------------------------------------------------

def _build_hint_feedback(exec_ctx: ExecutionCtx, hints: List[Hint]) -> str:
    lines = ["[Python 反馈]:"]
    if exec_ctx.rejected:
        for date_str, code, detail in exec_ctx.rejected:
            lines.append(f"  [{date_str}] {code}: {detail}")
    if exec_ctx.invalid_cmds:
        lines.append("  @command 错误:")
        for ic in exec_ctx.invalid_cmds:
            lines.append(f"    {ic}")
    lines.append("")
    lines.append(f"[state] round={exec_ctx.round_num}, remaining={len(exec_ctx.remaining)}")
    return "\n".join(lines)


def _build_initial_user_prompt(
    orch_ctx: OrchestratorContext,
    total_dates: List[date],
    windows: List[TimeWindow],
) -> str:
    date_range = f"{total_dates[0].isoformat()} ~ {total_dates[-1].isoformat()}"
    window_desc = "\n".join(
        f"  - {w.name}: {w.dates[0].isoformat()} ~ {w.dates[-1].isoformat()} ({len(w.dates)}天)"
        for w in windows
    )
    return f"""请为以下排班任务制定计划。

日期范围：{date_range}（共 {len(total_dates)} 天）
区域：{', '.join(orch_ctx.all_areas)}
规则：{orch_ctx.duty_rule}

已分解为 {len(windows)} 个时间窗口：
{window_desc}

请为每个窗口分配可用学生，并输出完整的 INI 格式排班方案。
输出 @finalize 表示完成。
""".strip()


def _finalize(
    ctx: Context,
    exec_ctx: ExecutionCtx,
    orch_ctx: OrchestratorContext,
    emit_progress_fn: Optional[Callable],
) -> Dict[str, Any]:
    """将最终状态写入 state.json."""
    def _apply_state(current_state: Dict[str, Any]) -> Dict[str, Any]:
        state_data = dict(current_state)
        state_data["next_run_note"] = f"mode=orchestrator; slots={len(exec_ctx.schedule_pool)}; rounds={exec_ctx.round_num}"
        state_data["debt_counts"] = dict(exec_ctx.debt_counts)
        state_data["credit_counts"] = dict(exec_ctx.credit_counts)
        state_data["last_pointer"] = exec_ctx.last_pointer
        state_data["schedule_pool"] = list(exec_ctx.schedule_pool)
        state_data["debt_counts"], state_data["credit_counts"] = resolve_debt_credit_conflicts(
            state_data["debt_counts"],
            state_data["credit_counts"],
        )
        return state_data

    state_data = update_state(ctx.paths["state"], _apply_state, stop_event=None)

    _emit_progress(emit_progress_fn, "orchestrator_done", "State written.", {
        "schedule_entries": len(exec_ctx.schedule_pool),
        "finalized": exec_ctx.finalized,
    })

    return {
        "status": "ok",
        "schedule_entries": exec_ctx.schedule_pool,
        "rejected": exec_ctx.rejected,
        "invalid_cmds": exec_ctx.invalid_cmds,
        "final_state": {
            "debt_counts": dict(exec_ctx.debt_counts),
            "credit_counts": dict(exec_ctx.credit_counts),
            "last_pointer": exec_ctx.last_pointer,
        },
        "execution_plan": {"runtime_mode": "orchestrator"},
        "orchestrator_meta": {
            "rounds": exec_ctx.round_num,
            "finalized": exec_ctx.finalized,
            "hints_on": orch_ctx.hints_on,
        },
        "trace_id": orch_ctx.trace_id,
    }


# ------------------------------------------------------------------------------
# 日期/区域/状态 辅助
# ------------------------------------------------------------------------------

def _compute_dates(instruction: str, start_date: date, default_days: int = 7) -> List[date]:
    date_ranges = _parse_date_ranges(instruction, start_date)
    if date_ranges:
        all_dates: List[date] = []
        for s, e in date_ranges:
            d = s
            while d <= e:
                all_dates.append(d)
                d += timedelta(days=1)
        return sorted(set(all_dates))
    return [start_date + timedelta(days=i) for i in range(default_days)]


_DATE_RANGE_RE = re.compile(
    r"(\d{1,2})[月/-](\d{1,2})(?:\s*日?\s*[-~至到]\s*(\d{1,2})[月/-](\d{1,2})(?:\s*日?))?",
)
_DATE_SINGLE_RE = re.compile(r"(\d{1,2})[月/-](\d{1,2})")


def _parse_date_ranges(text: str, default_year: int) -> List[Tuple[date, date]]:
    results: List[Tuple[date, date]] = []
    for m in _DATE_RANGE_RE.finditer(text):
        mm1, dd1 = int(m.group(1)), int(m.group(2))
        mm2, dd2 = int(m.group(3)), int(m.group(4))
        try:
            d1 = date(default_year, mm1, dd1)
            d2 = date(default_year, mm2, dd2)
            if d1 <= d2:
                results.append((d1, d2))
            else:
                results.append((d2, d1))
        except ValueError:
            pass
    return results


def _get_area_names(config: dict) -> List[str]:
    areas = config.get("areas", [])
    if areas:
        return list(areas)
    return [DEFAULT_SINGLE_AREA_NAME]


def _get_area_per_day_counts(config: dict, area_names: List[str]) -> Dict[str, int]:
    counts: Dict[str, int] = {}
    area_counts_cfg = config.get("area_per_day_counts", {})
    if isinstance(area_counts_cfg, dict):
        for area in area_names:
            counts[area] = int(area_counts_cfg.get(area, area_counts_cfg.get("default", 2)))
    else:
        default = int(area_counts_cfg or 2)
        for area in area_names:
            counts[area] = default
    return counts


def _build_required_slots(
    instruction: str,
    dates: List[date],
    id_to_name: Dict[int, str],
    area_names: List[str],
) -> List[ScheduleUnit]:
    area_per_day = _get_area_per_day_counts_from_areas(area_names)
    slots = []
    for d in dates:
        for area in area_names:
            need = area_per_day.get(area, 2)
            for i in range(need):
                slots.append(ScheduleUnit(
                    date_iso=d.isoformat(),
                    area_name=area,
                    alias=area[:1].upper(),
                ))
    return slots


def _get_area_per_day_counts_from_areas(area_names: List[str]) -> Dict[str, int]:
    return {area: 2 for area in area_names}


def _expand_debt_counts(debt_counts: Dict[int, int]) -> List[int]:
    result: List[int] = []
    for pid, cnt in debt_counts.items():
        result.extend([pid] * int(cnt))
    return result


def _expand_credit_counts(credit_counts: Dict[int, int]) -> List[int]:
    return _expand_debt_counts(credit_counts)


def _emit_progress(
    emit_progress_fn: Optional[Callable],
    phase: str,
    message: str,
    payload: Dict[str, Any],
) -> None:
    if not emit_progress_fn:
        return
    try:
        import json
        emit_progress_fn(phase, message, json.dumps(payload, ensure_ascii=False))
    except Exception:
        pass
