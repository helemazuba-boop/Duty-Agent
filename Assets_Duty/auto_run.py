# -*- coding: utf-8 -*-
"""Pure trigger helpers for the backend auto-run worker (A1).

``runtime.check_auto_run`` polls every minute and asks two questions:

* :func:`is_auto_run_triggered` — is *today* a scheduled run day? The caller
  layers its own dedup (``last_auto_run_date != today``) and the
  ``auto_run_time`` gate on top, so this function only answers the calendar
  question.
* :func:`detect_catchup_due` — was a scheduled day *strictly before today*
  missed (crash / shutdown / sleep)? Catch-up bypasses the time gate, so it
  deliberately excludes "today is the run day" — otherwise every scheduled
  morning would fire before ``auto_run_time`` and the gate would be useless.

Mode/parameter semantics mirror the C# settings page
(``DutyMainSettingsPage.GetSelectedAutoRunParameter``):

* ``Weekly``  — parameter is a weekday: full name ("Monday"), 3-letter
  ("mon"), or number ("1".."7", with "0"/"7" = Sunday).
* ``Monthly`` — parameter is a day-of-month ("1".."31") or "L" (last day);
  days beyond the month length clamp to the last day.
* ``Custom``  — parameter is an interval in days since the last run
  (default 14, minimum 1).
* ``Off`` / unknown — never triggers.

All functions are pure (no I/O, no clock access) for testability.
"""

from __future__ import annotations

from calendar import monthrange
from datetime import date, datetime, timedelta

# Scheduled days older than this are considered expired rather than caught up
# (keep in sync with runtime.AUTO_RUN_CATCHUP_MAX_LOOKBACK_DAYS).
CATCHUP_MAX_LOOKBACK_DAYS = 31

DEFAULT_CUSTOM_INTERVAL_DAYS = 14

_WEEKDAY_ALIASES = {
    "monday": 0, "mon": 0, "1": 0,
    "tuesday": 1, "tue": 1, "2": 1,
    "wednesday": 2, "wed": 2, "3": 2,
    "thursday": 3, "thu": 3, "4": 3,
    "friday": 4, "fri": 4, "5": 4,
    "saturday": 5, "sat": 5, "6": 5,
    "sunday": 6, "sun": 6, "7": 6, "0": 6,
}


def _normalize_mode(mode: object) -> str:
    return str(mode or "").strip().lower()


def _parse_weekday(parameter: object) -> int:
    """Weekday index (Monday=0). Unknown values fall back to Monday, mirroring
    the C# settings page default."""
    return _WEEKDAY_ALIASES.get(str(parameter or "").strip().lower(), 0)


def _monthly_scheduled_day(parameter: object, year: int, month: int) -> int:
    """Scheduled day-of-month for ``Monthly`` mode ("L" = last day; numeric
    days clamp to the month length; garbage falls back to the last day)."""
    last_day = monthrange(year, month)[1]
    text = str(parameter or "").strip().lower()
    if text == "l":
        return last_day
    try:
        day = int(text)
    except (TypeError, ValueError):
        return last_day
    return max(1, min(day, last_day))


def _parse_custom_interval(parameter: object) -> int:
    try:
        interval = int(str(parameter or "").strip())
    except (TypeError, ValueError):
        return DEFAULT_CUSTOM_INTERVAL_DAYS
    return max(1, interval)


def _parse_iso_date(value: object) -> date | None:
    text = str(value or "").strip()
    if not text:
        return None
    try:
        return date.fromisoformat(text)
    except ValueError:
        return None


def _is_scheduled_day(mode: str, parameter: object, day: date) -> bool:
    if mode == "weekly":
        return day.weekday() == _parse_weekday(parameter)
    if mode == "monthly":
        return day.day == _monthly_scheduled_day(parameter, day.year, day.month)
    return False


def is_auto_run_triggered(mode: object, parameter: object, last_run_date: object, now: datetime) -> bool:
    """Return True when *today* (``now.date()``) is a scheduled run day.

    The caller handles same-day dedup and the ``auto_run_time`` gate.
    """
    normalized = _normalize_mode(mode)
    today = now.date()

    if normalized in {"weekly", "monthly"}:
        return _is_scheduled_day(normalized, parameter, today)

    if normalized == "custom":
        last = _parse_iso_date(last_run_date)
        if last is None:
            # Never ran (or unparseable history): due immediately.
            return True
        return (today - last).days >= _parse_custom_interval(parameter)

    return False


def detect_catchup_due(mode: object, parameter: object, last_run_date: object, today: date) -> bool:
    """Return True when a scheduled day strictly between ``last_run_date`` and
    ``today`` was missed (bounded by :data:`CATCHUP_MAX_LOOKBACK_DAYS`).

    ``today`` itself is intentionally NOT treated as catch-up: on the scheduled
    day the normal trigger + ``auto_run_time`` gate govern. With no ``last``
    baseline there is nothing to catch up (the normal path handles first runs).
    """
    normalized = _normalize_mode(mode)
    if normalized not in {"weekly", "monthly", "custom"}:
        return False

    last = _parse_iso_date(last_run_date)
    if last is None or last >= today:
        return False

    if normalized == "custom":
        # Strictly overdue: the scheduled day (last + N) already passed.
        return (today - last).days > _parse_custom_interval(parameter)

    for offset in range(1, CATCHUP_MAX_LOOKBACK_DAYS + 1):
        candidate = today - timedelta(days=offset)
        if candidate <= last:
            break
        if _is_scheduled_day(normalized, parameter, candidate):
            return True
    return False
