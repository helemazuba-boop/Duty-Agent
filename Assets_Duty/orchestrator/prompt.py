"""
System prompt builders for the Orchestrator and TimeWindow Agent.

Orchestrator has two mutually exclusive contracts:
  - direct  (total_slots < 7): Orchestrator itself outputs the full INI plan.
  - poll    (total_slots >= 7): Orchestrator only writes per-window dispatch
    directives ([[#window]] blocks); the actual INI comes from TimeWindow
    agents and is validated by Python. Asking the Orchestrator for a full INI
    here would waste generation tokens on output that is never parsed.
"""
from __future__ import annotations

from collections import Counter
from datetime import date
from typing import Dict, List

from .context import OrchestratorContext, TimeWindow


# ------------------------------------------------------------------------------
# Orchestrator Agent
# ------------------------------------------------------------------------------

def build_orchestrator_system_prompt(
    ctx: OrchestratorContext,
    total_dates: List[date],
    mode: str = "direct",
) -> str:
    date_strs = [d.isoformat() for d in total_dates]

    debt_text = ctx.format_debt_list()
    credit_text = ctx.format_credit_list()
    person_pool = ctx.format_person_pool()

    hints_directive = _hints_directive_block(ctx.hints_on)
    previous_note_block = _previous_note_block(ctx.previous_note)

    shared_state = f"""## 当前任务
- 请求时间：{ctx.request_time.isoformat()}
- 开始日期：{ctx.start_date.isoformat()}
- 日期范围：{date_strs[0]} ~ {date_strs[-1]}（共 {len(total_dates)} 天）

## 当前状态
- 债务人员（优先安排）：{debt_text}
- 信用人员（可优先）：{credit_text}
- 不活跃人员：{', '.join(str(p) for p in ctx.inactive_ids) or '无'}
- 请假/缺席人员（本窗口不可安排）：{', '.join(str(p) for p in ctx.absent_ids) or '无'}
{_day_overrides_block(ctx.day_overrides)}
## 可用学生池
{person_pool}

## 排班规则（严格遵守）
{ctx.duty_rule}
"""

    if mode == "poll":
        return f"""你是排班协调 Orchestrator。填充工作由按时间窗口分工的 Agent 完成，
你负责：为每个窗口写下分配指令；收到 Python 反馈后修正有问题的窗口指令。
你不直接生成排班 INI，也不负责持久化。

{shared_state}
## 输出格式：窗口指令（不是排班）

为需要调整的窗口各写一个指令块，块内用简洁的中文列表描述分配要点：
[[#phase1]]
- 优先安排债务: 1001, 1002
- 04-03 A区需要 3 人（班级活动）

不要输出状态区块、待办清单或任何 @ 指令，
也不要输出日期=人员的排班行——那由窗口 Agent 生成、Python 校验。

{hints_directive}{previous_note_block}""".strip()

    command_directive = _command_directive_block(ctx.hints_on)
    remaining_manifest = _render_remaining_template(ctx, total_dates)

    return f"""你是排班调度 Orchestrator。你的职责是根据用户指令和当前状态，
直接生成完整排班方案（INI 格式），由 Python 校验并持久化。

{shared_state}
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
覆盖下面 [remaining] 清单中的每一个 (日期, 区域)。行格式示例：
{total_dates[0].isoformat()} = {_area_alias(ctx.all_areas[0])}:ID1 ID2{_render_extra_area_example(ctx)}

### [remaining] 清单（每行 = 一个待填 (日期, 区域)，需 N 人 = 该区域每日人数）
{remaining_manifest}

{command_directive}
## 注意事项

1. 每天每个区域都需要安排 {fmt_areas_need(ctx)} 人
2. 不要重复安排同一人同一天
3. 债务人员必须优先分配
4. 遵守所有排班规则约束
5. 如果某天某区域无法安排满，注明原因

{hints_directive}{previous_note_block}""".strip()


def build_orchestrator_round_prompt(
    ctx: OrchestratorContext,
    round_num: int,
    hints: List[object],
    python_response_ini: str = "",
    mode: str = "direct",
) -> str:
    """Orchestrator 第 N 轮反馈提示词（含 Python 反馈和 hints）.

    poll 模式下不回传完整权威 INI（Orchestrator 不排班，只需要 hints），
    以免每轮对话线性膨胀。"""
    hint_lines = []
    for h in hints:
        level = getattr(h, "level", "INFO")
        msg = getattr(h, "message", str(h))
        hint_lines.append(f"; {level}: {msg}")

    hints_block = "\n".join(hint_lines)
    if hints_block:
        hints_block = f"; hints:\n{hints_block}\n"

    if mode == "poll":
        return f"""; === 第 {round_num} 轮 Python 反馈 ===
{hints_block}
; 请只更新被 hints 点名窗口的 [[#窗口名]] 指令块；其余窗口不要重复输出。
""".strip()

    return f"""; === 第 {round_num} 轮 ===
{python_response_ini}

{hints_block}
; 请根据上述状态和 hints，修正 INI 输出。
; 输出完整的 [state] + [[#batch]] + [remaining]。
; 使用 @keep/@drop/@replace 调整已有批次。
; 确认无误后输出 @finalize。
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

## 优先债务（必须优先安排这些学生，N*表示需安排 N 次）
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
    parts = []
    for pid, cnt in Counter(pid_list).items():
        parts.append(f"{pid}*{cnt}" if cnt > 1 else str(pid))
    return " ".join(parts)


def _render_extra_area_example(ctx: OrchestratorContext) -> str:
    if len(ctx.all_areas) < 2:
        return ""
    second = ctx.all_areas[1]
    return f" | {_area_alias(second)}:ID3 ID4"


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
        tag_str = f" [{', '.join(tags)}]" if tags else ""
        name = ctx.id_to_name.get(pid, str(pid))
        lines.append(f"  {name} (ID={pid}){tag_str}")
    return "\n".join(lines) if lines else "  （无可用人员）"


def _format_window_debt(window: TimeWindow) -> str:
    """债务 ID 列表带出现次数（列表里每个条目 = 需安排一次）。"""
    if not window.debt_ids:
        return "无"
    parts = []
    for pid, cnt in Counter(window.debt_ids).items():
        parts.append(f"ID={pid}*{cnt}" if cnt > 1 else f"ID={pid}")
    return ", ".join(parts)


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


def _previous_note_block(previous_note: str) -> str:
    note = str(previous_note or "").strip()
    if not note:
        return ""
    return f"""
## 上一轮备注（来自上次排班的遗留说明）
{note}
"""


def _day_overrides_block(day_overrides: Dict[str, Dict[str, int]]) -> str:
    overrides = day_overrides or {}
    if not overrides:
        return ""
    lines = ["- 单日人数覆盖（该日该区域按下表人数安排，如大扫除加派）："]
    for day in sorted(overrides):
        for area, count in sorted(overrides[day].items()):
            lines.append(f"  - {day} {area}：{count} 人")
    return "\n".join(lines) + "\n"


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
