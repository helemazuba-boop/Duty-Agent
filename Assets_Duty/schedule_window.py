"""Resolve a natural-language date scope into an explicit list of ISO dates.

The single-pass backend used to audit every run against a fixed ``default_days``
calendar window starting today. That cannot represent "今天及下周值日": the
following Saturday falls outside 7 days, while an unrequested Sunday falls
inside and is reported as a missing date. ``strict`` validation mode even
refused to commit an otherwise correct schedule.

This module keeps the legacy behaviour for instructions that do NOT ask for a
week-scale span, and only derives an explicit date list when the instruction
clearly names a week. The same list is injected into the prompt (so the model
never has to guess weekdays) and into ``resume_context.expected_dates`` (which
``apply_single_pass_completion`` already knows how to consume).
"""

from __future__ import annotations

from datetime import date, timedelta
from typing import List

_NEXT_WEEK_TOKENS = ("下周", "下星期", "下个星期", "next week", "nextweek")
_THIS_WEEK_TOKENS = ("本周", "这周", "整周", "this week")
_TODAY_TOKENS = ("今天", "今日", "从今天", "today")
_SATURDAY_TOKENS = ("周六", "星期六", "周末", "saturday")
_SUNDAY_TOKENS = ("周日", "周天", "星期天", "星期日", "周末", "sunday")


def _contains(text: str, tokens) -> bool:
    lowered = text.lower()
    return any(token.lower() in lowered for token in tokens)


def _legacy_window(start_date: date, default_days: int) -> List[str]:
    days = max(1, int(default_days or 7))
    return [(start_date + timedelta(days=offset)).isoformat() for offset in range(days)]


def resolve_schedule_dates(
    instruction: str,
    start_date: date,
    default_days: int = 7,
    duty_rule: str = "",
) -> List[str]:
    """Return the exact ISO dates this run is expected to cover.

    Only week-scale instructions change the window. In that case weekends not
    mentioned by ``duty_rule`` are skipped, because a rule such as
    "周一至周五两个区域, 周六教室2人" describes no Sunday duty at all.
    """
    text = str(instruction or "").strip()
    rule = str(duty_rule or "")
    legacy = _legacy_window(start_date, default_days)
    if not text:
        return legacy

    has_next_week = _contains(text, _NEXT_WEEK_TOKENS)
    has_this_week = _contains(text, _THIS_WEEK_TOKENS)
    if not (has_next_week or has_this_week):
        return legacy

    has_today = _contains(text, _TODAY_TOKENS)
    week_monday = start_date - timedelta(days=start_date.weekday())
    if has_next_week:
        begin = start_date if has_today else week_monday + timedelta(days=7)
        end = week_monday + timedelta(days=13)  # Sunday of next week
    else:
        begin = start_date
        end = week_monday + timedelta(days=6)  # Sunday of this week

    include_saturday = _contains(rule, _SATURDAY_TOKENS)
    include_sunday = _contains(rule, _SUNDAY_TOKENS)
    dates: List[str] = []
    cursor = begin
    while cursor <= end:
        weekday = cursor.weekday()  # Mon=0 .. Sun=6
        if (weekday == 5 and not include_saturday) or (weekday == 6 and not include_sunday):
            cursor += timedelta(days=1)
            continue
        dates.append(cursor.isoformat())
        cursor += timedelta(days=1)
    return dates or legacy
