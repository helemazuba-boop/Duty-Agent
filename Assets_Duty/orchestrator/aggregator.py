from __future__ import annotations

import re
from typing import Dict, List, Tuple

from .context import OrchestratorContext, TimeWindow


def concatenate_ini_fragments(
    fragments: List[Tuple[TimeWindow, str]],
    ctx: OrchestratorContext,
) -> str:
    """
    将多个 TimeWindow Agent 的 INI 片段拼接为完整 INI 文本。
    同时补充 [state] 和 [remaining] 区块。

    fragments: [(window, ini_text), ...]
    """
    lines: List[str] = []

    lines.append("[state]")
    lines.append(f"round = 1")
    lines.append(f"pointer = {ctx.last_pointer}")
    from .prompt import fmt_count_map
    lines.append(f"debt = {fmt_count_map(ctx.debt_list)}")
    lines.append(f"credit = {fmt_count_map(ctx.credit_list)}")
    lines.append("")

    for window, fragment in fragments:
        fragment = fragment.strip()
        if not fragment:
            lines.append(f"[[#{window.name}]]")
            lines.append(f"; TIMEOUT: {window.name} 无输出，留空")
            lines.append("")
            continue

        fragment_clean = _strip_leading_batch_comment(fragment)
        lines.append(f"[[#{window.name}]]")
        lines.append(fragment_clean)
        lines.append("")

    lines.append("[remaining]")
    assigned = _extract_assigned_ids(fragments)
    for d in _get_all_dates(ctx):
        for area in ctx.all_areas:
            key = (d.isoformat(), area)
            need = ctx.area_per_day_counts.get(area, 0)
            filled = len(assigned.get(key, []))
            alias = _area_alias(area)
            if filled < need:
                lines.append(f"{d.isoformat()} = {alias}:?（需{need}人）")
            else:
                ids_str = " ".join(str(i) for i in assigned.get(key, []))
                lines.append(f"{d.isoformat()} = {alias}:{ids_str}")
    lines.append("")

    lines.append("; 可用人员:")
    for pid in ctx.active_ids:
        name = ctx.id_to_name.get(pid, str(pid))
        area_tag = ctx.id_to_area.get(pid, "")
        debt_tag = " 债务" if pid in ctx.debt_list else ""
        lines.append(f";   {name}(ID={pid}){debt_tag}{area_tag}")

    return "\n".join(lines)


def _strip_leading_batch_comment(text: str) -> str:
    """去掉片段开头可能重复的 [[#batchname]] 标注行."""
    lines = text.splitlines()
    cleaned = []
    seen_batch = False
    for line in lines:
        if re.match(r"\[\[#.+?\]\]", line.strip()):
            if not seen_batch:
                seen_batch = True
            continue
        cleaned.append(line)
    return "\n".join(cleaned).strip()


def _extract_assigned_ids(
    fragments: List[Tuple[TimeWindow, str]],
) -> Dict[Tuple[str, str], List[int]]:
    """从 INI 片段中提取所有已安排的人员 ID."""
    assigned: Dict[Tuple[str, str], List[int]] = {}
    for window, text in fragments:
        if not text.strip():
            continue
        for line in text.splitlines():
            line = line.strip()
            if not line or line.startswith(";") or line.startswith("#"):
                continue
            date_match = re.match(r"(\d{4}-\d{2}-\d{2})\s*=\s*(.+)", line)
            if date_match:
                date_str = date_match.group(1)
                areas_part = date_match.group(2)
                for area_chunk in areas_part.split("|"):
                    area_chunk = area_chunk.strip()
                    m = re.match(r"([^:]+):(.+)", area_chunk)
                    if m:
                        area = m.group(1).strip()
                        ids_str = m.group(2).strip()
                        if ids_str and ids_str != "?":
                            ids = [
                                int(x.strip())
                                for x in re.findall(r"\d+", ids_str)
                            ]
                            key = (date_str, area)
                            if key not in assigned:
                                assigned[key] = []
                            for pid in ids:
                                if pid not in assigned[key]:
                                    assigned[key].append(pid)
    return assigned


def _get_all_dates(ctx: OrchestratorContext) -> List:
    """从 ctx 获取所有日期（由 executor 传入，这里返回占位列表）."""
    return getattr(ctx, "_all_dates", [])


def _area_alias(area_name: str) -> str:
    aliases = {"A区": "A", "B区": "B", "C区": "C", "教室": "A", "操场": "B", "图书馆": "C"}
    return aliases.get(area_name, area_name[:1].upper())
