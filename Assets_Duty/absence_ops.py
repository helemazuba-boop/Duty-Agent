# -*- coding: utf-8 -*-
"""Pure helpers for sudden-situation handling: leave/absence extraction, run
notes, day headcount overrides, and deterministic shortfall/excess repair.

Everything here is stdlib-only and free of I/O and clock access (``today`` is
always an explicit argument with a ``date.today()`` fallback at the entry
points that need it), so it can be imported by state_ops and every executor
without import cycles, and unit-tested without fixtures.
"""
from __future__ import annotations

import re
from datetime import date, timedelta
from typing import Dict, Iterable, List, Optional, Sequence, Tuple


# ------------------------------------------------------------------------------
# Absence extraction from natural-language instructions
# ------------------------------------------------------------------------------

ABSENCE_KEYWORDS: Tuple[str, ...] = (
    "请假", "缺席", "病假", "事假", "生病", "生病了", "病了", "不舒服",
    "发烧", "感冒", "不在", "离开", "休息",
)

_CLAUSE_SPLIT_RE = re.compile(r"[，。；！？!?,;\n\r]+")
# NOTE: 、 is deliberately NOT a clause separator — "张三、李四请假" must stay
# in one clause so both names are captured.

_CN_NUM = {"一": 1, "二": 2, "两": 2, "三": 3, "四": 4, "五": 5,
           "六": 6, "七": 7, "八": 8, "九": 9, "十": 10}

_DAYS_RE = re.compile(r"([0-9一二两三四五六七八九十]+)\s*(?:个)?\s*(?:天|日)")
_WEEKS_RE = re.compile(r"([0-9一二两三四五六七八九十]+)\s*(?:个)?\s*(?:星期|周|礼拜)")
_UNTIL_FULL_RE = re.compile(r"到\s*(\d{4})-(\d{1,2})-(\d{1,2})")
_UNTIL_MD_RE = re.compile(r"到\s*(\d{1,2})[月./-](\d{1,2})[日号]?")
_TODAY_RE = re.compile(r"今天|今日")
_TOMORROW_RE = re.compile(r"明天|明日")
_DAY_AFTER_RE = re.compile(r"后天")


def _cn_number(token: str) -> Optional[int]:
    token = token.strip()
    if not token:
        return None
    if token.isdigit():
        return int(token)
    if token == "十":
        return 10
    total = 0
    if token.startswith("十"):
        total = 10
        rest = token[1:]
    elif "十" in token:
        head, _, rest = token.partition("十")
        head_value = _CN_NUM.get(head)
        if head_value is None:
            return None
        total = head_value * 10
    else:
        rest = token
    if rest:
        rest_value = _CN_NUM.get(rest)
        if rest_value is None:
            return None
        total += rest_value
    return total if total > 0 else None


def _parse_until(values: Tuple[int, ...], today: date) -> Optional[date]:
    try:
        if len(values) == 3:
            parsed = date(values[0], values[1], values[2])
        else:
            parsed = date(today.year, values[0], values[1])
            if parsed <= today:
                parsed = date(today.year + 1, values[0], values[1])
        return parsed
    except ValueError:
        return None


def _clause_absence_range(clause: str, today: date, default_days: int) -> Optional[Tuple[date, date]]:
    """Return (from, to) for one clause already known to contain an absence
    keyword. Explicit "到 MM-DD / YYYY-MM-DD" wins, then "N 天/周", then
    今天/明天/后天 offsets, then ``default_days`` from today (D-C)."""
    match = _UNTIL_FULL_RE.search(clause)
    if match:
        until = _parse_until(tuple(int(g) for g in match.groups()), today)
        if until is not None and until >= today:
            return today, until

    match = _UNTIL_MD_RE.search(clause)
    if match:
        until = _parse_until(tuple(int(g) for g in match.groups()), today)
        if until is not None and until >= today:
            return today, until

    start = today
    if _DAY_AFTER_RE.search(clause):
        start = today + timedelta(days=2)
    elif _TOMORROW_RE.search(clause):
        start = today + timedelta(days=1)

    days: Optional[int] = None
    match = _DAYS_RE.search(clause)
    if match:
        days = _cn_number(match.group(1))
    if days is None:
        match = _WEEKS_RE.search(clause)
        if match:
            weeks = _cn_number(match.group(1))
            if weeks is not None:
                days = weeks * 7
    if days is not None and days > 0:
        return start, start + timedelta(days=days - 1)

    return start, start + timedelta(days=max(1, int(default_days)) - 1)


def extract_absentees(
    raw_instruction: str,
    name_to_id: Dict[str, int],
    today: Optional[date] = None,
    default_days: int = 1,
) -> List[Dict[str, object]]:
    """Extract absentees from a raw (pre-anonymization) instruction.

    A roster name counts as absent only when it shares a clause with an absence
    keyword, so "张三请假，李四补上" does not mark 李四. Runs BEFORE
    ``anonymize_instruction`` (which rewrites names into IDs).
    """
    today = today or date.today()
    text = str(raw_instruction or "")
    if not text.strip() or not name_to_id:
        return []

    names_sorted = sorted(name_to_id.keys(), key=len, reverse=True)
    found: Dict[int, Tuple[date, date]] = {}
    for clause in _CLAUSE_SPLIT_RE.split(text):
        clause = clause.strip()
        if not clause or not any(keyword in clause for keyword in ABSENCE_KEYWORDS):
            continue
        window = clause
        matched: List[Tuple[str, int]] = []
        for name in names_sorted:
            if name and name in window:
                matched.append((name, name_to_id[name]))
                window = window.replace(name, "\x00")
        if not matched:
            continue
        span = _clause_absence_range(clause, today, default_days)
        if span is None:
            continue
        for _, person_id in matched:
            previous = found.get(person_id)
            if previous is None or span[0] < previous[0] or span[1] > previous[1]:
                found[person_id] = span

    return [
        {"id": person_id, "from": span[0].isoformat(), "to": span[1].isoformat()}
        for person_id, span in sorted(found.items())
    ]


def _parse_iso(value: object) -> Optional[date]:
    try:
        return date.fromisoformat(str(value or "").strip()[:10])
    except ValueError:
        return None


def merge_absence_entries(
    existing: Iterable[dict],
    additions: Iterable[dict],
) -> List[Dict[str, object]]:
    """Union absence ranges per person; overlapping/adjacent ranges merge."""
    by_id: Dict[int, List[Tuple[date, date]]] = {}
    for entry in list(existing or []) + list(additions or []):
        if not isinstance(entry, dict):
            continue
        try:
            person_id = int(entry.get("id"))
        except (TypeError, ValueError):
            continue
        start = _parse_iso(entry.get("from"))
        end = _parse_iso(entry.get("to"))
        if start is None or end is None or end < start:
            continue
        by_id.setdefault(person_id, []).append((start, end))

    merged: List[Dict[str, object]] = []
    for person_id in sorted(by_id):
        ranges = sorted(by_id[person_id])
        current_start, current_end = ranges[0]
        for start, end in ranges[1:]:
            if start <= current_end + timedelta(days=1):
                current_end = max(current_end, end)
            else:
                merged.append({"id": person_id, "from": current_start.isoformat(), "to": current_end.isoformat()})
                current_start, current_end = start, end
        merged.append({"id": person_id, "from": current_start.isoformat(), "to": current_end.isoformat()})
    return merged


def absences_for_window(
    absences: Iterable[dict],
    window_start: date,
    window_end: date,
) -> List[int]:
    """Person IDs whose absence range overlaps [window_start, window_end]."""
    result: List[int] = []
    for entry in absences or []:
        if not isinstance(entry, dict):
            continue
        start = _parse_iso(entry.get("from"))
        end = _parse_iso(entry.get("to"))
        if start is None or end is None:
            continue
        if start <= window_end and end >= window_start:
            try:
                person_id = int(entry.get("id"))
            except (TypeError, ValueError):
                continue
            if person_id not in result:
                result.append(person_id)
    return result


# ------------------------------------------------------------------------------
# Run notes (machine summary + user notes with expiry)
# ------------------------------------------------------------------------------

def active_user_notes(state: dict, today: Optional[date] = None) -> List[dict]:
    today = today or date.today()
    result: List[dict] = []
    for note in (state or {}).get("user_notes", []) or []:
        if not isinstance(note, dict):
            continue
        text = str(note.get("text", "") or "").strip()
        if not text:
            continue
        until = _parse_iso(note.get("until"))
        if until is not None and until < today:
            continue
        result.append(note)
    return result


def compose_run_notes(state: dict, today: Optional[date] = None) -> str:
    """Machine summary (``next_run_note``) + unexpired ``user_notes``."""
    parts: List[str] = []
    machine = str((state or {}).get("next_run_note", "") or "").strip()
    if machine:
        parts.append(machine)
    for note in active_user_notes(state, today):
        parts.append(str(note.get("text", "")).strip())
    return "\n".join(parts)


# ------------------------------------------------------------------------------
# Day headcount overrides
# ------------------------------------------------------------------------------

def day_override_count(
    overrides: Optional[dict],
    date_iso: str,
    area_name: str,
    default: int,
) -> int:
    day_map = (overrides or {}).get(date_iso) or {}
    try:
        value = int(day_map.get(area_name, default))
    except (TypeError, ValueError):
        return int(default)
    return value if value > 0 else int(default)


# ------------------------------------------------------------------------------
# Deterministic shortfall/excess repair (D-A)
# ------------------------------------------------------------------------------

def rotation_order(
    all_ids: Sequence[int],
    id_to_active: Dict[int, int],
    last_pointer: int,
    exclude_ids: Iterable[int] = (),
) -> List[int]:
    """Active IDs in ring order starting at ``last_pointer`` — mirrors
    offline_scheduler.build_active_rotation without its import weight."""
    total = len(all_ids)
    if total == 0:
        return []
    start = max(0, min(int(last_pointer or 0), total - 1))
    exclude = set(exclude_ids)
    result: List[int] = []
    for offset in range(total):
        person_id = int(all_ids[(start + offset) % total])
        if id_to_active.get(person_id, 1) != 0 and person_id not in exclude:
            result.append(person_id)
    return result


def fill_shortfall(
    current_ids: Sequence[int],
    required_count: Optional[int],
    candidate_order: Sequence[int],
    debt_counts: Optional[Dict[int, int]] = None,
) -> Tuple[List[int], List[int]]:
    """Fill an under-staffed area up to ``required_count``.

    Candidates are consumed debt-first (D-A), then in the given (rotation)
    order; people already assigned are skipped. Returns (ids, added)."""
    ids = list(current_ids)
    added: List[int] = []
    if required_count is None or len(ids) >= int(required_count):
        return ids, added
    present = set(ids)
    ordered = list(candidate_order)
    if debt_counts:
        debtors = [p for p in ordered if int(debt_counts.get(p, 0) or 0) > 0]
        others = [p for p in ordered if int(debt_counts.get(p, 0) or 0) <= 0]
        ordered = debtors + others
    for person_id in ordered:
        if len(ids) >= int(required_count):
            break
        if person_id in present:
            continue
        ids.append(person_id)
        added.append(person_id)
        present.add(person_id)
    return ids, added


def trim_excess(
    current_ids: Sequence[int],
    required_count: Optional[int],
    debt_counts: Optional[Dict[int, int]] = None,
    protected_ids: Iterable[int] = (),
) -> Tuple[List[int], List[int]]:
    """Trim an over-staffed area back to ``required_count``.

    Removal starts from the tail of the assignment list and skips debtors and
    explicitly protected people first (D-A); if the list is still over, the
    tail is trimmed unconditionally. Returns (kept, removed)."""
    ids = list(current_ids)
    if required_count is None or len(ids) <= int(required_count):
        return ids, []
    removed: List[int] = []
    protected = set(protected_ids)

    def _is_protected(person_id: int) -> bool:
        if person_id in protected:
            return True
        return bool(debt_counts and int(debt_counts.get(person_id, 0) or 0) > 0)

    index = len(ids) - 1
    while len(ids) > int(required_count) and index >= 0:
        if not _is_protected(ids[index]):
            removed.append(ids.pop(index))
        index -= 1
    while len(ids) > int(required_count):
        removed.append(ids.pop())
    return ids, removed
