"""
System prompt builder and tool description for the tool-loop executor.
"""
from __future__ import annotations

from datetime import date
from typing import Dict, List


# ------------------------------------------------------------------------------
# Tool metadata (OpenAI function calling format)
# ------------------------------------------------------------------------------

TOOL_DESCRIPTION = """fill_schedule: Submit INI-formatted schedule for validation and persistence.

input schema:
  schedule: string -- full INI text with optional [[#batch]] blocks, [areas], [schedule], [state]

Returns the updated INI template with Python's authoritative [state].
"""


# ------------------------------------------------------------------------------
# Format helpers
# ------------------------------------------------------------------------------

def _format_ids(values: List[int]) -> str:
    return " ".join(str(pid) for pid in values)


def _format_count_map(counts: Dict[int, int]) -> str:
    if not counts:
        return ""
    parts: List[str] = []
    for pid in sorted(counts.keys()):
        cnt = int(counts.get(pid, 0) or 0)
        if cnt <= 0:
            continue
        parts.append(f"{pid}*{cnt}" if cnt > 1 else str(pid))
    return " ".join(parts)


def _build_boundary_hints(start_date_text: str) -> str:
    hints: List[str] = []
    try:
        start = date.fromisoformat(start_date_text.strip())
        next_month = date(start.year, start.month, 1)
        for _ in range(16):
            if next_month.month == 12:
                month_end = date(next_month.year, 12, 31)
                following = date(next_month.year + 1, 1, 1)
            else:
                following = date(next_month.year, next_month.month + 1, 1)
                month_end = date(next_month.year, next_month.month - 1, 12) if next_month.month > 1 else date(next_month.year - 1, 12, 31)
            if month_end >= start:
                hints.append(f"{month_end:%m-%d}->{following:%m-%d}")
            if len(hints) >= 3:
                break
            next_month = date(next_month.year, next_month.month + 1, 1) if next_month.month < 12 else date(next_month.year + 1, 1, 1)
    except Exception:
        pass
    if "12-31->01-01" not in hints:
        hints.append("12-31->01-01")
    return "boundary_dates=" + ", ".join(dict.fromkeys(hints))


# ------------------------------------------------------------------------------
# Base system prompt
# ------------------------------------------------------------------------------

def build_tool_system_prompt(
    instruction: str,
    duty_rule: str,
    all_ids: List[int],
    inactive_ids: List[int],
    debt_counts: Dict[int, int],
    credit_counts: Dict[int, int],
    last_pointer: int,
    start_date: str,
    current_time: str,
) -> str:
    """
    Build the system prompt for the tool-loop AI session.
    This is the preamble injected before every AI turn.
    """
    inactive_str = _format_ids(inactive_ids)
    debt_str = _format_count_map(debt_counts)
    credit_str = _format_count_map(credit_counts)
    compact = len(all_ids) <= 30

    duty_rule = str(duty_rule or "").strip()

    lines: List[str] = [
        "You are Duty-Agent (tool mode).",
        "You generate duty schedules by calling the fill_schedule tool.",
        "Python processes your INI output: validates entries, simulates debt/credit/pointer,",
        "and returns the authoritative [state].",
        "Your job is to produce INI text until the schedule is complete.",
        "",
    ]

    # Context
    lines.append("Context:")
    lines.append(f"  all_roster_ids={_format_ids(all_ids)}")
    lines.append(f"  current_time={current_time}")
    lines.append(f"  start_date={start_date}")
    lines.append(f"  {_build_boundary_hints(start_date)}")
    lines.append(f"  last_pointer={max(0, int(last_pointer or 0))}")

    if inactive_str:
        lines.append(f"  inactive_ids={inactive_str}")

    if debt_str:
        lines.append(f"  current_debt_counts={debt_str}")

    if credit_str:
        lines.append(f"  current_credit_counts={credit_str}")

    lines.append(f"  user_instruction={instruction}")
    lines.append("")

    # Rules
    lines.append("Scheduling rules:")
    lines.append("- Debt priority: if an available ID has unresolved debt, prefer assigning that ID early.")
    lines.append("  One scheduled appearance clears only one debt count.")
    lines.append("- Credit rule: when the normal roster progression reaches a credited ID,")
    lines.append("  skip that ID once if possible. Credit is consumed only when that skip happens.")
    lines.append("- Inactive IDs are unavailable and must never be assigned.")
    lines.append("- Only generate the exact requested dates. Over-generation is fatal.")

    if duty_rule:
        lines.append(f"- User-defined rule: {duty_rule}")

    lines.append("")
    lines.append("Output format:")
    lines.append("Output an INI template. Use [[#batch]] blocks to group related schedule lines.")
    lines.append("Declare [areas] aliases before using them in [schedule].")
    lines.append("")
    lines.append("--- [[#batch]] blocks ---")
    lines.append("Wrap each group of related dates in [[#batchname]] / [[#batchname-drop]] markers.")
    lines.append("  [[#week1]]")
    lines.append("  04-01 = A:1001 1002 | B:1003 1004")
    lines.append("  04-02 = A:1005 1006 | B:1007 1008")
    lines.append("  [[#week1-drop]]   -- marks entries in week1 as invalid (slot released)")
    lines.append("")
    lines.append("--- [areas] ---")
    lines.append("Declare area aliases. One per line:")
    lines.append("  A = 教室")
    lines.append("  B = 清洁区")
    lines.append("Declare every alias used in [schedule] here first.")
    lines.append("")
    lines.append("--- [schedule] ---")
    lines.append("One line per date, format: MM-DD = <alias>:<id> <id> | <alias>:<id> # <optional note>")
    lines.append("  04-06 = A:1001 1002 | B:1003 1004 # 备注")
    lines.append("Dates must be in ascending order. Use MM-DD format. Python resolves the year.")
    lines.append("Inside one alias assignment list, never repeat the same ID.")
    lines.append("Same-day cross-area reuse is allowed. Same-area duplicate IDs are forbidden.")
    lines.append("")
    lines.append("--- [state] ---")
    lines.append("Python overwrites your [state] with the authoritative values.")
    lines.append("Declare your expected state as a reference:")
    lines.append("  pointer = <int>")
    lines.append("  debt = <id>*<count> ...")
    lines.append("  credit = <id>*<count> ...")
    lines.append("")
    lines.append("Return the INI template by calling the fill_schedule tool.")
    lines.append("After the tool returns, read the authoritative [state] carefully.")
    lines.append("Continue adding more schedule lines until all dates are covered.")
    lines.append("When complete, call fill_schedule with @finalize to commit.")

    if compact:
        lines.append("")
        lines.append("[compact mode] Use short aliases (A, B, S). Output only what changes.")

    lines.append("")
    lines.append("No markdown fences. No XML. No JSON. No reasoning text in the INI output.")
    lines.append("Output the INI template directly.")

    return "\n".join(lines)


# ------------------------------------------------------------------------------
# Optional hints injection (only when hints_on=True)
# ------------------------------------------------------------------------------

def build_hints_prompt() -> str:
    """
    Return the optional @command hints block.
    Injected into the system prompt when hints_on=True.
    When hints_on=False, this is omitted entirely.
    """
    lines: List[str] = []
    lines.append("")
    lines.append("--- Batch control commands ---")
    lines.append("You can reference and adjust batches using these special commands:")
    lines.append("")
    lines.append("  @keep(#week1 04-01 A)        -- keep only 04-01 A区的 slot, release others")
    lines.append("  @keep(#week1)                -- keep all slots in week1")
    lines.append("  @drop(#week2)                -- discard entire week2, release all its slots")
    lines.append("  @replace(#week1 04-02 B=1009)  -- replace 04-02 B区 with person 1009")
    lines.append("  @finalize                   -- commit the schedule and stop polling")
    lines.append("")
    lines.append("Place these commands in your INI output (outside any section).")
    lines.append("Python processes them in order: @keep → @drop → @replace → @finalize.")
    lines.append("If you output no commands, all slots in your batches are kept.")
    return "\n".join(lines)
