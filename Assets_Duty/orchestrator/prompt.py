"""
System prompt builders for the Orchestrator and TimeWindow Agent.
"""
from __future__ import annotations

from datetime import date
from typing import Dict, List

from .context import OrchestratorContext, TimeWindow


# ------------------------------------------------------------------------------
# Orchestrator Agent
# ------------------------------------------------------------------------------

def build_orchestrator_system_prompt(ctx: OrchestratorContext, total_dates: List[date]) -> str:
    date_strs = [d.isoformat() for d in total_dates]

    debt_text = ctx.format_debt_list()
    credit_text = ctx.format_credit_list()
    person_pool = ctx.format_person_pool()

    hints_directive = _hints_directive_block(ctx.hints_on)
    command_directive = _command_directive_block(ctx.hints_on)

    return f"""你是排班调度 Orchestrator。你的职责是根据用户指令和当前状态，
生成完整排班方案（INI 格式）并持久化。

## 当前任务
- 请求时间：{ctx.request_time.isoformat()}
- 开始日期：{ctx.start_date.isoformat()}
- 轮次：1（所有学生轮完一次 = 一个完整轮次）
- 日期范围：{date_strs[0]} ~ {date_strs[-1]}（共 {len(total_dates)} 天）
- 轮询轮次：第 1 轮

## 当前状态
- 债务人员（优先安排）：{debt_text}
- 信用人员（可优先）：{credit_text}
- 不活跃人员：{', '.join(str(p) for p in ctx.inactive_ids) or '无'}

## 可用学生池
{person_pool}

## 排班规则（严格遵守）
{ctx.duty_rule}

## 输出格式：INI 文本

使用 INI 格式输出排班方案。用 [[#batchname]] 标注批次，用 [state] 声明状态，
用 [remaining] 列出尚未安排的班次（每轮 Python 会重新计算）。

### [state] 区块（参考值，Python 会在校验后覆盖）
[state]
round = 1
pointer = {ctx.last_pointer}
debt = {fmt_count_map(ctx.debt_list)}
credit = {fmt_count_map(ctx.credit_list)}

### [[#batch]] 区块（你的主要输出）
[[#batch1]]
{_render_schedule_template(ctx, total_dates)}

[remaining]
{_render_remaining_template(ctx, total_dates)}

{command_directive}

{fmt_person_pool_for_remaining(ctx)}

## 注意事项

1. 每天每个区域都需要安排 {fmt_areas_need(ctx)} 人
2. 不要重复安排同一人同一天
3. 债务人员必须优先分配
4. 遵守所有排班规则约束
5. 如果某天某区域无法安排满，注明原因

{hints_directive}
""".strip()


def build_orchestrator_round_prompt(
    ctx: OrchestratorContext,
    total_dates: List[date],
    round_num: int,
    python_response_ini: str,
    hints: List[object],
) -> str:
    """Orchestrator 第 N 轮的系统提示词（含 Python 反馈和 hints）."""
    hint_lines = []
    for h in hints:
        level = getattr(h, "level", "INFO")
        msg = getattr(h, "message", str(h))
        hint_lines.append(f"; {level}: {msg}")

    hints_block = "\n".join(hint_lines)
    if hints_block:
        hints_block = f"; hints:\n{hints_block}\n"

    return f"""; === 第 {round_num} 轮 ===
{python_response_ini}

{hints_block}
; 请根据上述状态和 hints，修正 INI 输出。
; 输出完整的 [state] + [[#batch]] + [remaining]。
; 使用 @keep/@drop/@replace 调整已有批次。
; 确认无误后输出 @finalize。
""".strip()


def build_orchestrator_finalize_prompt(ctx: OrchestratorContext) -> str:
    """Orchestrator 输出 @finalize 后的确认提示词."""
    return """; @finalize 已收到。INI 解析器正在持久化。
; 排班完成。
""".strip()


# ------------------------------------------------------------------------------
# TimeWindow Agent
# ------------------------------------------------------------------------------

def build_timewindow_system_prompt(window: TimeWindow, ctx: OrchestratorContext) -> str:
    dates_str = ", ".join(d.isoformat() for d in window.dates)
    area_info = _format_area_info(window, ctx)
    person_pool = _format_timewindow_pool(window, ctx)
    debt_text = _format_window_debt(window)

    return f"""你是排班填充 Agent，只负责你的专属时间窗口。
你不需要调用任何工具，直接在回复中输出 INI 文本即可。

## 你的时间窗口
- 窗口名称：{window.name}
- 日期范围：{dates_str}
- 包含天数：{len(window.dates)} 天

## 区域信息（每天每个区域需安排的人数）
{area_info}

## 可用学生
{person_pool}

## 优先债务（必须优先安排这些学生）
{debt_text}

## 排班规则
{ctx.duty_rule}

## 你的任务

为 [remaining] 中的每个 (日期, 区域) slot 分配学生。
直接输出 INI 格式的排班片段，不需要 [state] 区块。

## 输出格式

[[#{window.name}]]
{render_window_remaining(window, ctx)}

## 注意事项

1. 每天每个区域安排 {fmt_areas_need_from_window(window)} 人
2. 不要重复安排同一人同一天
3. 优先安排债务人员
4. 不要输出 @finalize（由 Orchestrator 决定）
5. 不要调用任何工具，只输出 INI 文本

## 格式示例

[[#{window.name}]]
{window.dates[0].isoformat()} = {window.area_names[0]}:ID1 ID2 | {window.area_names[1] if len(window.area_names) > 1 else window.area_names[0]}:ID3 ID4
{window.dates[1].isoformat()} = {window.area_names[0]}:ID5 ID6 | {window.area_names[1] if len(window.area_names) > 1 else window.area_names[0]}:ID7 ID8
""".strip()


def build_timewindow_retry_prompt(
    window: TimeWindow,
    ctx: OrchestratorContext,
    hint_message: str,
) -> str:
    """TimeWindow Agent 重试时的提示词（含错误信息）."""
    return f"""; === {window.name} 重试请求 ===
; 上一轮排班有问题，请修正：

; hint: {hint_message}

; 窗口范围：{', '.join(d.isoformat() for d in window.dates)}
; 区域需求：{fmt_areas_need_from_window(window)} 人/区域/天

请重新输出 INI 片段：
[[#{window.name}]]
{render_window_remaining(window, ctx)}
""".strip()


# ------------------------------------------------------------------------------
# Helpers
# ------------------------------------------------------------------------------

def fmt_count_map(pid_list: List[int]) -> str:
    """将 [1004, 1004, 1002] 格式化为 '1004*2 1002'."""
    if not pid_list:
        return ""
    from collections import Counter
    parts = []
    for pid, cnt in Counter(pid_list).items():
        parts.append(f"{pid}*{cnt}" if cnt > 1 else str(pid))
    return " ".join(parts)


def _render_schedule_template(ctx: OrchestratorContext, dates: List[date]) -> str:
    lines = []
    for d in dates:
        date_str = d.isoformat()
        areas_part = " | ".join(
            f"{_area_alias(area)}:?" for area in ctx.all_areas
        )
        lines.append(f"{date_str} = {areas_part}")
    return "\n".join(lines)


def _render_remaining_template(ctx: OrchestratorContext, dates: List[date]) -> str:
    lines = []
    for d in dates:
        date_str = d.isoformat()
        for area in ctx.all_areas:
            need = ctx.area_per_day_counts.get(area, 0)
            alias = _area_alias(area)
            lines.append(f"{date_str} = {alias}:?（需{need}人）")
    return "\n".join(lines)


def fmt_areas_need(ctx: OrchestratorContext) -> str:
    parts = []
    for area in ctx.all_areas:
        cnt = ctx.area_per_day_counts.get(area, 0)
        alias = _area_alias(area)
        parts.append(f"{alias}区({cnt}人)")
    return ", ".join(parts)


def fmt_areas_need_from_window(window: TimeWindow) -> str:
    parts = []
    for area in window.area_names:
        cnt = window.area_per_day.get(area, 0)
        alias = _area_alias(area)
        parts.append(f"{alias}区({cnt}人)")
    return ", ".join(parts)


def fmt_person_pool_for_remaining(ctx: OrchestratorContext) -> str:
    lines = ["; 可用人员:"]
    for pid in ctx.all_ids:
        if pid in ctx.inactive_ids:
            continue
        tags = []
        if pid in ctx.debt_list:
            tags.append("债务")
        if pid in ctx.credit_list:
            tags.append("信用+")
        if pid in ctx.id_to_area:
            tags.append(ctx.id_to_area[pid])
        tag_str = f" [{', '.join(tags)}]" if tags else ""
        name = ctx.id_to_name.get(pid, str(pid))
        lines.append(f";   {name} (ID={pid}){tag_str}")
    return "\n".join(lines)


def _area_alias(area_name: str) -> str:
    aliases = {"A区": "A", "B区": "B", "C区": "C", "教室": "A", "操场": "B", "图书馆": "C"}
    return aliases.get(area_name, area_name[:1].upper())


def _format_area_info(window: TimeWindow, ctx: OrchestratorContext) -> str:
    lines = []
    for area in window.area_names:
        cnt = window.area_per_day.get(area, 0)
        alias = _area_alias(area)
        lines.append(f"  {area}（别名={alias}）：每天 {cnt} 人")
    return "\n".join(lines)


def _format_timewindow_pool(window: TimeWindow, ctx: OrchestratorContext) -> str:
    lines = []
    for pid in window.available_ids:
        tags = []
        if pid in window.debt_ids:
            tags.append("债务")
        if pid in window.credit_ids:
            tags.append("信用+")
        if pid in ctx.id_to_area:
            tags.append(ctx.id_to_area[pid])
        tag_str = f" [{', '.join(tags)}]" if tags else ""
        name = ctx.id_to_name.get(pid, str(pid))
        lines.append(f"  {name} (ID={pid}){tag_str}")
    return "\n".join(lines) if lines else "  （无可用人员）"


def _format_window_debt(window: TimeWindow) -> str:
    if not window.debt_ids:
        return "无"
    return ", ".join(f"ID={pid}" for pid in window.debt_ids)


def render_window_remaining(window: TimeWindow, ctx: OrchestratorContext) -> str:
    lines = []
    for d in window.dates:
        date_str = d.isoformat()
        areas_part = " | ".join(
            f"{_area_alias(area)}:?" for area in window.area_names
        )
        lines.append(f"{date_str} = {areas_part}")
    return "\n".join(lines)


def _hints_directive_block(hints_on: bool) -> str:
    if not hints_on:
        return ""
    return """
## Hints 修正指令（可选）
如有冲突或警告，Orchestrator 会在下一轮告知你（Hints 模式已开启）。
"""


def _command_directive_block(hints_on: bool) -> str:
    if not hints_on:
        return ""
    return """
## 特殊指令
- @keep(#batch SLOT)              保留指定 slot，其余释放
- @drop(#batch)                   丢弃整批，slot 全部释放
- @replace(#batch SLOT=NEW_ID)    替换人员
- @finalize                       提交并结束
"""
