"""Deterministic DATE FACTS and a loose upper bound for scheduling runs.

Which days actually need duty (weekends included or not) is a semantic decision
that belongs to the LLM. ``duty_rule`` is free-form natural language written for
the model (see build_prompt.py / multi_agent/prompts.py) and is deliberately
NEVER parsed here. Code contributes only:

* ``weekday_hints`` — a factual ``MM-DD=Mon`` table, so the model does not have
  to recompute weekdays from ``start_date`` by hand;
* ``date_span`` — a LOOSE upper bound that rejects obvious hallucinations
  (a date next year), NOT a checklist of dates that must all be filled.

``offline`` mode is the one legitimate exception: it has no LLM, so it owns its
own structured ``offline_skip_weekends`` / ``offline_schedule_days`` and emits
exact target dates. That path is deliberately untouched here.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import date, timedelta
from typing import Tuple

# Only used to WIDEN the allowed upper bound. They never produce a list of
# dates the model must schedule, and they never decide whether a weekend has
# duty — that is up to user_instruction / user_defined_rule and the LLM.
_NEXT_WEEK_TOKENS = ("下周", "下星期", "下个星期", "next week", "nextweek")
_THIS_WEEK_TOKENS = ("本周", "这周", "整周", "this week")
_VAGUE_SPAN_TOKENS = (
    "两周", "两个星期", "半个月", "一个月", "下个月", "连续", "几天", "多天", "到", "~", "至",
)
_WEEKDAY_NAMES = ("Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun")

_RELAXED_SPAN_DAYS = 30


def _contains(text: str, tokens) -> bool:
    lowered = text.lower()
    return any(token.lower() in lowered for token in tokens)


@dataclass(frozen=True)
class DateContext:
    """Facts and bounds only. Never a schedule.

    ``anchor_recognized`` says whether the instruction named a week-scale span;
    it is diagnostic and is used solely to widen ``span_end``. It must NOT be
    used to require coverage of any particular date.
    """

    anchor_recognized: bool
    span_start: date
    span_end: date
    weekday_hints: Tuple[str, ...]
    reason: str

    @property
    def date_span_text(self) -> str:
        return f"{self.span_start:%m-%d}..{self.span_end:%m-%d}"


def resolve_date_context(
    instruction: str,
    start_date: date,
    default_days: int = 7,
) -> DateContext:
    """Return a loose allowed date range plus a factual weekday table.

    No weekend/workday filtering happens here. The span is an upper bound for
    the hallucination guard, so an unparseable span hint is answered by
    WIDENING the bound rather than by guessing which days the user meant.
    """
    text = str(instruction or "").strip()
    lowered = text.lower()
    default_end = start_date + timedelta(days=max(1, int(default_days or 7)) - 1)
    week_monday = start_date - timedelta(days=start_date.weekday())

    anchor_recognized = False
    span_end = default_end
    reason = "default window"

    if _contains(lowered, _NEXT_WEEK_TOKENS):
        anchor_recognized = True
        span_end = week_monday + timedelta(days=13)  # Sunday of next week
        reason = "next-week anchor widened the upper bound"
    elif _contains(lowered, _THIS_WEEK_TOKENS):
        anchor_recognized = True
        span_end = week_monday + timedelta(days=6)  # Sunday of this week
        reason = "this-week anchor widened the upper bound"
    elif _contains(lowered, _VAGUE_SPAN_TOKENS):
        # "连续三天" / "11-10 到 11-20" cannot be resolved exactly here. Widen
        # the bound so strict validation cannot reject a legitimate schedule.
        span_end = start_date + timedelta(days=_RELAXED_SPAN_DAYS - 1)
        reason = "unparsed span hint; upper bound relaxed"

    span_end = max(span_end, default_end)
    day_count = (span_end - start_date).days + 1
    hints = tuple(
        f"{day:%m-%d}={_WEEKDAY_NAMES[day.weekday()]}"
        for day in (start_date + timedelta(days=offset) for offset in range(day_count))
    )
    return DateContext(
        anchor_recognized=anchor_recognized,
        span_start=start_date,
        span_end=span_end,
        weekday_hints=hints,
        reason=reason,
    )
