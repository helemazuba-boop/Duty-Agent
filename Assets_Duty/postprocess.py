from __future__ import annotations

from datetime import date, datetime
from typing import Dict, List, Optional

from state_ops import DEFAULT_PER_DAY, extract_ids_from_value

DAY_NAMES = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"]


def recover_missing_debts(original_debt_list: List[int], new_debt_ids_from_llm: List[int], normalized_schedule: List[dict]) -> List[int]:
    scheduled_set: set = set()
    for entry in normalized_schedule:
        if not isinstance(entry, dict):
            continue
        area_map = entry.get("area_ids", {})
        if not isinstance(area_map, dict):
            continue
        for ids in area_map.values():
            if isinstance(ids, list):
                scheduled_set.update(ids)

    final_debt_set = set(new_debt_ids_from_llm)
    for person_id in original_debt_list:
        if person_id not in scheduled_set:
            final_debt_set.add(person_id)
    final_debt_set -= scheduled_set
    return sorted(final_debt_set)


def reconcile_credit_list(
    original_credit_list: List[int],
    new_credit_ids_from_llm: List[int],
    normalized_schedule: List[dict],
    valid_ids: set,
    debt_list: List[int],
    has_llm_field: bool,
) -> List[int]:
    next_credit_set = set(new_credit_ids_from_llm) if has_llm_field else set(original_credit_list)
    next_credit_set = {credit_id for credit_id in next_credit_set if credit_id in valid_ids}
    next_credit_set -= set(debt_list)
    return sorted(next_credit_set)


def extract_area_ids(entry, area_name, area_index, active_set, per_area_count):
    result = []

    def append_ids(value):
        nonlocal result
        if len(result) >= per_area_count:
            return
        for person_id in extract_ids_from_value(value, active_set, per_area_count):
            if person_id not in result:
                result.append(person_id)
                if len(result) >= per_area_count:
                    break

    for key in ("area_ids", "areas", "area_assignments"):
        mapping = entry.get(key)
        if isinstance(mapping, dict):
            append_ids(mapping.get(area_name))
            append_ids(mapping.get(str(area_index)))
    return result


def normalize_multi_area_schedule_ids(
    schedule_raw: list,
    active_ids: List[int],
    area_names: List[str],
    area_per_day_counts: Dict[str, int],
) -> List[dict]:
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

        day_assignments: Dict[str, List[int]] = {}
        used_ids: set = set()
        for area_index, area_name in enumerate(area_names):
            per_area_count = area_per_day_counts.get(area_name, DEFAULT_PER_DAY)
            extracted_ids = extract_area_ids(entry, area_name, area_index, active_set, per_area_count)
            final_ids = [person_id for person_id in extracted_ids if person_id not in used_ids]
            day_assignments[area_name] = final_ids
            used_ids.update(final_ids)

        raw_area_ids = entry.get("area_ids") or entry.get("areas") or entry.get("area_assignments") or {}
        if isinstance(raw_area_ids, dict):
            for dynamic_area_name, raw_value in raw_area_ids.items():
                name = str(dynamic_area_name).strip()
                if not name or name in day_assignments:
                    continue
                extracted = extract_ids_from_value(raw_value, active_set, None)
                final = [person_id for person_id in extracted if person_id not in used_ids]
                if final:
                    day_assignments[name] = final
                    used_ids.update(final)

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
