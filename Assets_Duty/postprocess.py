from __future__ import annotations

from datetime import date, datetime
from typing import Dict, Iterable, List, Optional, Sequence

from state_ops import clone_count_map, decrement_count_map_entry, increment_count_map_entry, normalize_count_map, resolve_debt_credit_conflicts

DAY_NAMES = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"]


def collect_scheduled_unique_ids(normalized_schedule: List[dict]) -> List[int]:
    scheduled: List[int] = []
    seen: set[int] = set()
    for entry in normalized_schedule:
        if not isinstance(entry, dict):
            continue
        area_map = entry.get("area_ids", {})
        if not isinstance(area_map, dict):
            continue
        for ids in area_map.values():
            if not isinstance(ids, list):
                continue
            for person_id in ids:
                try:
                    normalized_id = int(person_id)
                except (TypeError, ValueError):
                    continue
                if normalized_id in seen:
                    continue
                seen.add(normalized_id)
                scheduled.append(normalized_id)
    return scheduled


def recover_missing_debts(original_debt_list, new_debt_ids_from_llm, normalized_schedule: List[dict]) -> Dict[int, int]:
    next_debt_counts = clone_count_map(original_debt_list)
    for person_id in collect_scheduled_unique_ids(normalized_schedule):
        decrement_count_map_entry(next_debt_counts, person_id)
    for person_id, count in normalize_count_map(new_debt_ids_from_llm).items():
        increment_count_map_entry(next_debt_counts, person_id, count)
    return next_debt_counts


def reconcile_credit_list(
    original_credit_list,
    new_credit_ids_from_llm,
    normalized_schedule: List[dict],
    valid_ids: set,
    debt_list,
    has_llm_field: bool,
    consumed_credit_ids: Optional[Iterable[int]] = None,
) -> Dict[int, int]:
    del normalized_schedule
    del has_llm_field

    next_credit_counts = clone_count_map(original_credit_list, valid_ids)
    for person_id in normalize_count_map(list(consumed_credit_ids or []), valid_ids).keys():
        decrement_count_map_entry(next_credit_counts, person_id)

    for person_id, count in normalize_count_map(new_credit_ids_from_llm, valid_ids).items():
        increment_count_map_entry(next_credit_counts, person_id, count)

    _, next_credit_counts = resolve_debt_credit_conflicts(
        normalize_count_map(debt_list, valid_ids),
        next_credit_counts,
    )
    return next_credit_counts


def estimate_pointer_progress(
    all_ids: Sequence[int],
    active_ids: Sequence[int],
    last_pointer: int,
    debt_counts,
    credit_counts,
    normalized_schedule: List[dict],
) -> Dict[str, object]:
    if not all_ids:
        return {"consumed_credit_ids": [], "pointer_after": 0}

    active_set = set(int(person_id) for person_id in active_ids)
    debt_seed = set(normalize_count_map(debt_counts, active_set).keys())
    credit_seed = set(normalize_count_map(credit_counts, active_set).keys())
    target_ids = {
        person_id
        for person_id in collect_scheduled_unique_ids(normalized_schedule)
        if person_id in active_set and person_id not in debt_seed
    }
    if not target_ids:
        return {"consumed_credit_ids": [], "pointer_after": max(0, min(int(last_pointer or 0), len(all_ids) - 1))}

    cursor = max(0, min(int(last_pointer or 0), len(all_ids) - 1))
    matched_ids: set[int] = set()
    consumed_credit_ids: List[int] = []
    max_steps = max(len(all_ids) * 3, len(target_ids) * 4)

    for _ in range(max_steps):
        person_id = int(all_ids[cursor])
        cursor = (cursor + 1) % len(all_ids)

        if person_id not in active_set:
            continue
        if person_id in target_ids and person_id not in matched_ids:
            matched_ids.add(person_id)
            if matched_ids == target_ids:
                break
            continue
        if person_id in credit_seed and person_id not in consumed_credit_ids:
            consumed_credit_ids.append(person_id)

    return {
        "consumed_credit_ids": consumed_credit_ids,
        "pointer_after": cursor,
    }


def _resolve_area_mapping(entry: dict) -> Dict[str, object]:
    for key in ("area_ids", "areas", "area_assignments"):
        mapping = entry.get(key)
        if isinstance(mapping, dict):
            return mapping
    return {}


def _coerce_area_value_items(value: object) -> List[object]:
    if value is None:
        return []
    if isinstance(value, str):
        stripped = value.strip()
        if not stripped:
            return []
        return stripped.split()
    if isinstance(value, list):
        return list(value)
    if isinstance(value, (int, float)):
        return [value]
    raise ValueError(f"unsupported area value type: {type(value).__name__}")


def _parse_area_ids(value: object, active_set: set[int], *, date_str: str, area_name: str) -> List[int]:
    result: List[int] = []
    seen: set[int] = set()
    for raw in _coerce_area_value_items(value):
        text = str(raw).strip()
        if not text:
            continue
        try:
            person_id = int(text)
        except Exception as ex:
            raise ValueError(f"invalid ID '{text}' on {date_str}/{area_name}") from ex
        if person_id not in active_set:
            raise ValueError(f"unknown or inactive ID {person_id} on {date_str}/{area_name}")
        if person_id in seen:
            raise ValueError(f"duplicate ID {person_id} on {date_str}/{area_name}")
        seen.add(person_id)
        result.append(person_id)
    return result


def normalize_multi_area_schedule_ids(
    schedule_raw: list,
    active_ids: List[int],
    area_names: List[str],
    area_per_day_counts: Dict[str, int],
) -> List[dict]:
    del area_per_day_counts
    active_set = set(active_ids)
    normalized: List[dict] = []
    if not isinstance(schedule_raw, list):
        return []
    for entry in schedule_raw:
        if not isinstance(entry, dict):
            continue
        raw_date = str(entry.get("date", "")).strip()
        if len(raw_date) < 8:
            continue

        area_mapping = _resolve_area_mapping(entry)
        day_assignments: Dict[str, List[int]] = {}
        for area_name in area_names:
            raw_value = area_mapping.get(area_name)
            day_assignments[area_name] = _parse_area_ids(
                raw_value,
                active_set,
                date_str=raw_date,
                area_name=area_name,
            )

        for dynamic_area_name, raw_value in area_mapping.items():
            name = str(dynamic_area_name).strip()
            if not name or name in day_assignments or name.isdigit():
                continue
            day_assignments[name] = _parse_area_ids(
                raw_value,
                active_set,
                date_str=raw_date,
                area_name=name,
            )

        normalized.append(
            {
                "date": raw_date,
                "area_ids": day_assignments,
                "note": str(entry.get("note", "")).strip(),
            }
        )
    return normalized


def restore_schedule(
    normalized_ids: List[dict],
    id_to_name: Dict[int, str],
    area_names: List[str],
    existing_notes: Optional[Dict[str, str]] = None,
) -> List[dict]:
    restored = []
    existing_notes = existing_notes or {}
    for day_entry in normalized_ids:
        date_str = day_entry.get("date", "")
        try:
            entry_date = datetime.strptime(date_str, "%Y-%m-%d").date()
        except Exception:
            continue
        area_ids_map = day_entry.get("area_ids", {})
        area_assignments: Dict[str, List[str]] = {}
        for name in area_names:
            area_assignments[name] = [
                id_to_name[person_id]
                for person_id in area_ids_map.get(name, [])
                if person_id in id_to_name
            ]
        for name, ids in area_ids_map.items():
            if name not in area_assignments:
                students = [id_to_name[person_id] for person_id in ids if person_id in id_to_name]
                if students:
                    area_assignments[name] = students
        note = str(day_entry.get("note", "")).strip() or existing_notes.get(date_str, "")
        restored.append(
            {
                "date": date_str,
                "day": DAY_NAMES[entry_date.weekday()],
                "area_assignments": area_assignments,
                "note": note,
            }
        )
    return restored


def merge_schedule_pool(state_data: dict, restored: List[dict], apply_mode: str, start_date: date) -> List[dict]:
    pool = [entry for entry in state_data.get("schedule_pool", []) if isinstance(entry, dict)]
    restored = [entry for entry in restored if isinstance(entry, dict)]
    if apply_mode == "append":
        return dedupe_pool_by_date(pool + restored)
    if apply_mode == "replace_all":
        return dedupe_pool_by_date(restored)
    if apply_mode == "replace_future":
        kept = [entry for entry in pool if (parsed := try_parse_iso_date(entry.get("date"))) is None or parsed < start_date]
        return dedupe_pool_by_date(kept + restored)
    if apply_mode == "replace_overlap":
        dates = [parsed for entry in restored if (parsed := try_parse_iso_date(entry.get("date"))) is not None]
        if not dates:
            return dedupe_pool_by_date(pool + restored)
        end_date = max(dates)
        kept = [
            entry
            for entry in pool
            if (parsed := try_parse_iso_date(entry.get("date"))) is None or parsed < start_date or parsed > end_date
        ]
        return dedupe_pool_by_date(kept + restored)
    return dedupe_pool_by_date(pool + restored)


def dedupe_pool_by_date(entries):
    by_date = {str(entry.get("date", "")).strip(): entry for entry in entries if str(entry.get("date", "")).strip()}
    return [by_date[key] for key in sorted(by_date.keys())]


def try_parse_iso_date(value):
    try:
        return datetime.strptime(str(value or "").strip(), "%Y-%m-%d").date()
    except Exception:
        return None


def validate_llm_schedule_entries(schedule_raw):
    if not isinstance(schedule_raw, list):
        raise ValueError("schedule must be a list.")
    previous_date = None
    seen = set()
    for index, entry in enumerate(schedule_raw):
        date_str = str(entry.get("date", "")).strip()
        if not date_str:
            raise ValueError(f"entry {index} missing date.")
        try:
            current_date = datetime.strptime(date_str, "%Y-%m-%d").date()
        except Exception as ex:
            raise ValueError(f"entry {index} invalid date {date_str}.") from ex
        if previous_date and current_date < previous_date:
            raise ValueError("dates must be sorted.")
        if current_date in seen:
            raise ValueError(f"duplicate date {date_str} at {index}.")
        previous_date = current_date
        seen.add(current_date)
