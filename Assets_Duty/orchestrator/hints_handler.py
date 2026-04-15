from __future__ import annotations

from typing import Any, Dict, List, Tuple, Optional

from .context import Hint


def parse_hints_from_execution_ctx(
    exec_ctx: Any,
) -> List[Hint]:
    """
    从 ini_handler 的 ExecutionCtx.rejected 中提取 hints，
    转换为 Hint 对象列表。
    """
    hints: List[Hint] = []
    for date_str, reason_code, detail in exec_ctx.rejected:
        level = _reason_to_level(reason_code)
        msg = f"[{date_str}] {reason_code}: {detail}" if date_str else f"{reason_code}: {detail}"
        hints.append(Hint(level=level, message=msg, slot_key=date_str))
    return hints


def parse_hints_from_ini_text(ini_text: str) -> List[Hint]:
    """
    从 INI 文本中的 ; hints: / ; CONFLICT: / ; WARNING: 注释行解析 hints。
    """
    hints: List[Hint] = []
    import re
    for line in ini_text.splitlines():
        line = line.strip()
        m = re.match(r";\s*(CONFLICT|WARNING|INFO)\s*[:：]?\s*(.*)", line, re.IGNORECASE)
        if m:
            level = m.group(1).upper()
            message = m.group(2).strip()
            hints.append(Hint(level=level, message=message))
    return hints


def _reason_to_level(reason_code: str) -> str:
    """
    将 reason_code 映射为 Hint level.

    reason_code 示例：
      - "PERSON_NOT_AVAILABLE"
      - "CONSECUTIVE_DAY_VIOLATION"
      - "DUPLICATE_PERSON"
      - "UNDERSTAFFED"
    """
    CONFLICT_CODES = {
        "PERSON_NOT_AVAILABLE",
        "DUPLICATE_PERSON",
        "CONSECUTIVE_DAY_VIOLATION",
        "SAME_AREA_SAME_DAY",
        "INVALID_ID",
        "RULE_VIOLATION",
    }
    WARNING_CODES = {
        "UNDERSTAFFED",
        "OVERSTAFFED",
        "POOL_EXHAUSTED",
    }
    if reason_code in CONFLICT_CODES:
        return "CONFLICT"
    if reason_code in WARNING_CODES:
        return "WARNING"
    return "INFO"


def classify_hints(
    hints: List[Hint],
) -> Tuple[List[Hint], List[Hint], List[Hint]]:
    """将 hints 按 level 分类."""
    conflicts = [h for h in hints if h.level == "CONFLICT"]
    warnings = [h for h in hints if h.level == "WARNING"]
    infos = [h for h in hints if h.level == "INFO"]
    return conflicts, warnings, infos


def determine_retry_strategy(
    hints: List[Hint],
) -> dict[str, Any]:
    """
    根据 hints 分类决定重试策略。

    Returns:
        {
            "action": "retry_all" | "retry_windows" | "orchestrator_fix" | "finalize",
            "retry_windows": List[int],   # 需要重试的窗口索引
            "reason": str,
        }
    """
    conflicts, warnings, infos = classify_hints(hints)

    if not hints or all(h.level == "INFO" for h in hints):
        return {"action": "finalize", "reason": "仅 info hints，可直接 finalize"}

    if len(conflicts) >= 3:
        return {
            "action": "orchestrator_fix",
            "reason": f"{len(conflicts)} 个冲突，由 Orchestrator 直接修正",
        }

    if conflicts:
        window_map = _group_hints_by_window(conflicts)
        if len(window_map) > 1:
            return {
                "action": "retry_windows",
                "retry_windows": list(window_map.keys()),
                "reason": f"冲突分散在 {len(window_map)} 个窗口，分别重试",
            }
        else:
            return {
                "action": "retry_windows",
                "retry_windows": list(window_map.keys()),
                "reason": f"冲突集中在 {len(conflicts)} 个窗口，重试对应窗口",
            }

    if warnings:
        return {
            "action": "orchestrator_fix",
            "reason": f"{len(warnings)} 个警告，由 Orchestrator 补全",
        }

    return {"action": "finalize", "reason": "无可处理的 hints"}


def _group_hints_by_window(hints: List[Hint]) -> Dict[int, List[Hint]]:
    """将 hints 按 window index 分组."""
    grouped: Dict[int, List[Hint]] = {}
    for h in hints:
        idx = h.affects_window if h.affects_window is not None else 0
        if idx not in grouped:
            grouped[idx] = []
        grouped[idx].append(h)
    return grouped


def build_retry_context(
    hints: List[Hint],
    windows: List[Any],
) -> Dict[int, str]:
    """
    为每个需要重试的窗口构建 retry hint 合并文本。
    Returns: {window_index: merged_hint_message}
    """
    grouped = _group_hints_by_window(hints)
    retry_context: Dict[int, str] = {}
    for idx, group in grouped.items():
        messages = [h.message for h in group]
        retry_context[idx] = "\n".join(f"  - {m}" for m in messages)
    return retry_context
