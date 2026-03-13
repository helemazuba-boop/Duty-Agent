from __future__ import annotations

from datetime import datetime, timedelta
from typing import Any, Dict, List, Sequence

from .contracts import FrozenSnapshot

SUPPORTED_RULE_TYPES = {
    "fixed_area",
    "avoid_same_day_pair",
    "bind_same_day",
    "avoid_consecutive_days",
}


def _ensure_list_of_ints(values: Any, valid_ids: set[int] | None = None) -> List[int]:
    if values is None:
        return []
    if not isinstance(values, list):
        raise ValueError("expected a list of IDs")
    result: List[int] = []
    for raw in values:
        try:
            person_id = int(raw)
        except Exception as ex:
            raise ValueError(f"invalid person id: {raw}") from ex
        if valid_ids is not None and person_id not in valid_ids:
            raise ValueError(f"unknown person id: {person_id}")
        if person_id not in result:
            result.append(person_id)
    return result


def _ensure_string_list(values: Any) -> List[str]:
    if values is None:
        return []
    if not isinstance(values, list):
        raise ValueError("expected a string list")
    return [str(item).strip() for item in values if str(item).strip()]


def validate_agent1_output(raw: Dict[str, Any], snapshot: FrozenSnapshot) -> Dict[str, Any]:
    dates = _ensure_string_list(raw.get("dates"))
    if not dates:
        raise ValueError("agent1 dates is empty")

    parsed_dates = []
    for item in dates:
        try:
            parsed = datetime.strptime(item, "%Y-%m-%d").date()
        except ValueError as ex:
            raise ValueError(f"invalid date: {item}") from ex
        if parsed < snapshot.request_time.date():
            raise ValueError(f"date is before request day: {item}")
        parsed_dates.append(parsed)
    if parsed_dates != sorted(parsed_dates):
        raise ValueError("dates must be sorted ascending")
    if len(set(dates)) != len(dates):
        raise ValueError("dates must be unique")

    template = raw.get("template")
    if not isinstance(template, dict):
        raise ValueError("agent1 template must be an object")
    if set(template.keys()) != set(dates):
        raise ValueError("template keys must exactly match dates")

    normalized_template: Dict[str, Dict[str, int]] = {}
    area_names: List[str] = []
    total_slots = 0
    for date_text in dates:
        areas = template.get(date_text)
        if not isinstance(areas, dict) or not areas:
            raise ValueError(f"template for {date_text} is empty")
        normalized_areas: Dict[str, int] = {}
        for raw_area, raw_count in areas.items():
            area = str(raw_area).strip() or "default_area"
            try:
                count = int(raw_count)
            except Exception as ex:
                raise ValueError(f"invalid slot count for {date_text}/{area}") from ex
            if count <= 0:
                raise ValueError(f"slot count must be positive for {date_text}/{area}")
            normalized_areas[area] = count
            total_slots += count
            if area not in area_names:
                area_names.append(area)
        normalized_template[date_text] = normalized_areas

    declared_total = int(raw.get("total_slots", total_slots))
    if declared_total != total_slots:
        raise ValueError("total_slots mismatch")

    return {
        "dates": dates,
        "template": normalized_template,
        "area_names": area_names,
        "total_slots": total_slots,
    }


def validate_agent2_output(raw: Dict[str, Any], snapshot: FrozenSnapshot) -> Dict[str, Any]:
    valid_ids = set(snapshot.all_ids)
    absent_ids = _ensure_list_of_ints(raw.get("absent_ids", []), valid_ids)
    new_debt_ids = _ensure_list_of_ints(raw.get("new_debt_ids", []), valid_ids)
    new_credit_ids = _ensure_list_of_ints(raw.get("new_credit_ids", []), valid_ids)
    must_run_ids = _ensure_list_of_ints(raw.get("must_run_ids", []), valid_ids)
    volunteer_ids = _ensure_list_of_ints(raw.get("volunteer_ids", []), valid_ids)
    warnings = _ensure_string_list(raw.get("warnings", []))

    return {
        "absent_ids": absent_ids,
        "new_debt_ids": [person_id for person_id in new_debt_ids if person_id not in snapshot.debt_list],
        "new_credit_ids": new_credit_ids,
        "must_run_ids": must_run_ids,
        "volunteer_ids": volunteer_ids,
        "warnings": warnings,
    }


def _normalize_rule(rule: Dict[str, Any], valid_ids: set[int], area_names: Sequence[str] | None) -> Dict[str, Any]:
    if not isinstance(rule, dict):
        raise ValueError("rule must be an object")
    rule_type = str(rule.get("type", "")).strip()
    if rule_type not in SUPPORTED_RULE_TYPES:
        raise ValueError(f"unsupported rule type: {rule_type}")

    if rule_type == "fixed_area":
        person_id = int(rule.get("person_id"))
        area = str(rule.get("area", "")).strip()
        if person_id not in valid_ids:
            raise ValueError(f"unknown person for fixed_area: {person_id}")
        if not area:
            raise ValueError("fixed_area requires area")
        if area_names and area not in area_names:
            raise ValueError(f"unknown area for fixed_area: {area}")
        return {"type": rule_type, "person_id": person_id, "area": area}

    if rule_type in {"avoid_same_day_pair", "bind_same_day"}:
        person_a = int(rule.get("person_a"))
        person_b = int(rule.get("person_b"))
        if person_a not in valid_ids or person_b not in valid_ids:
            raise ValueError(f"unknown pair rule ids: {person_a}, {person_b}")
        if person_a == person_b:
            raise ValueError("pair rule cannot use the same person twice")
        return {"type": rule_type, "person_a": person_a, "person_b": person_b}

    person_id = int(rule.get("person_id"))
    if person_id not in valid_ids:
        raise ValueError(f"unknown person for avoid_consecutive_days: {person_id}")
    return {"type": rule_type, "person_id": person_id}


def validate_agent3_output(raw: Dict[str, Any], snapshot: FrozenSnapshot) -> Dict[str, Any]:
    valid_ids = set(snapshot.all_ids)
    supported_rules: List[Dict[str, Any]] = []
    for raw_rule in raw.get("supported_rules", []) or []:
        supported_rules.append(_normalize_rule(raw_rule, valid_ids, None))

    unsupported_rules = raw.get("unsupported_rules", [])
    if unsupported_rules is None:
        unsupported_rules = []
    if not isinstance(unsupported_rules, list):
        raise ValueError("unsupported_rules must be a list")
    warnings = _ensure_string_list(raw.get("warnings", []))

    return {
        "supported_rules": supported_rules,
        "unsupported_rules": unsupported_rules,
        "warnings": warnings,
    }


def merge_barrier1(snapshot: FrozenSnapshot, anchor: Dict[str, Any], accounting: Dict[str, Any], rules: Dict[str, Any]) -> Dict[str, Any]:
    active_ids = set(snapshot.active_ids)
    absent_ids = [person_id for person_id in accounting["absent_ids"] if person_id in active_ids]
    must_run_ids = [person_id for person_id in accounting["must_run_ids"] if person_id in active_ids and person_id not in absent_ids]
    if len(must_run_ids) > anchor["total_slots"]:
        raise ValueError("must_run_ids exceed total slots")

    supported_rules = [
        _normalize_rule(rule, set(snapshot.all_ids), anchor["area_names"])
        for rule in rules["supported_rules"]
    ]

    return {
        "dates": anchor["dates"],
        "template": anchor["template"],
        "area_names": anchor["area_names"],
        "total_slots": anchor["total_slots"],
        "absent_ids": absent_ids,
        "new_debt_ids": accounting["new_debt_ids"],
        "new_credit_ids": accounting["new_credit_ids"],
        "must_run_ids": must_run_ids,
        "volunteer_ids": accounting["volunteer_ids"],
        "supported_rules": supported_rules,
        "unsupported_rules": rules["unsupported_rules"],
        "warnings": accounting["warnings"] + rules["warnings"],
    }


def validate_agent4_output(raw: Dict[str, Any], snapshot: FrozenSnapshot, barrier1: Dict[str, Any]) -> Dict[str, Any]:
    active_ids = set(snapshot.active_ids)
    absent_ids = set(barrier1["absent_ids"])
    priority_pool = _ensure_list_of_ints(raw.get("priority_pool", []), active_ids)
    notes = _ensure_string_list(raw.get("notes", []))
    if len(priority_pool) > barrier1["total_slots"]:
        raise ValueError("priority_pool exceeds total slots")
    overlap = absent_ids.intersection(priority_pool)
    if overlap:
        raise ValueError(f"priority_pool contains absent ids: {sorted(overlap)}")
    return {
        "priority_pool": priority_pool,
        "notes": notes,
    }


def validate_agent5_output(raw: Dict[str, Any], snapshot: FrozenSnapshot, barrier1: Dict[str, Any]) -> Dict[str, Any]:
    active_ids = set(snapshot.active_ids)
    pointer_pool = _ensure_list_of_ints(raw.get("pointer_pool", []), active_ids)
    consumed_credit_ids = _ensure_list_of_ints(raw.get("consumed_credit_ids", []), set(snapshot.credit_list))
    notes = _ensure_string_list(raw.get("notes", []))
    try:
        pointer_after = int(raw.get("pointer_after", snapshot.last_pointer))
    except Exception as ex:
        raise ValueError("pointer_after must be an integer") from ex
    if snapshot.all_ids:
        pointer_after = max(0, min(pointer_after, len(snapshot.all_ids) - 1))
    else:
        pointer_after = 0

    overlap = set(pointer_pool).intersection(barrier1["absent_ids"])
    if overlap:
        raise ValueError(f"pointer_pool contains absent ids: {sorted(overlap)}")
    return {
        "pointer_pool": pointer_pool,
        "pointer_after": pointer_after,
        "consumed_credit_ids": consumed_credit_ids,
        "notes": notes,
    }


def build_slots(template: Dict[str, Dict[str, int]]) -> List[Dict[str, Any]]:
    slots: List[Dict[str, Any]] = []
    for date_text in sorted(template.keys()):
        for area_name, count in template[date_text].items():
            for slot_index in range(count):
                slots.append(
                    {
                        "slot_id": f"{date_text}::{area_name}::{slot_index}",
                        "date": date_text,
                        "area": area_name,
                    }
                )
    return slots


def merge_barrier2(snapshot: FrozenSnapshot, barrier1: Dict[str, Any], priority_result: Dict[str, Any], pointer_result: Dict[str, Any]) -> Dict[str, Any]:
    filtered_pointer_pool = [
        person_id
        for person_id in pointer_result["pointer_pool"]
        if person_id not in priority_result["priority_pool"]
    ]
    remaining_slots = barrier1["total_slots"] - len(priority_result["priority_pool"])
    final_pool = priority_result["priority_pool"] + filtered_pointer_pool[:max(0, remaining_slots)]
    if len(final_pool) != barrier1["total_slots"]:
        raise ValueError("final pool size does not match total slots")
    if len(set(final_pool)) != len(final_pool):
        raise ValueError("final pool contains duplicate IDs")

    return {
        "dates": barrier1["dates"],
        "template": barrier1["template"],
        "area_names": barrier1["area_names"],
        "total_slots": barrier1["total_slots"],
        "supported_rules": barrier1["supported_rules"],
        "unsupported_rules": barrier1["unsupported_rules"],
        "warnings": barrier1["warnings"] + priority_result["notes"] + pointer_result["notes"],
        "priority_pool": priority_result["priority_pool"],
        "pointer_pool": filtered_pointer_pool[:max(0, remaining_slots)],
        "final_pool": final_pool,
        "pointer_after": pointer_result["pointer_after"],
        "consumed_credit_ids": pointer_result["consumed_credit_ids"],
        "slots": build_slots(barrier1["template"]),
    }


def validate_agent6_output(raw: Dict[str, Any], barrier2: Dict[str, Any]) -> Dict[str, Any]:
    schedule = raw.get("schedule")
    if not isinstance(schedule, list) or not schedule:
        raise ValueError("agent6 schedule must be a non-empty list")
    normalized: List[Dict[str, Any]] = []
    valid_ids = set(barrier2["final_pool"])
    for entry in schedule:
        if not isinstance(entry, dict):
            raise ValueError("schedule entry must be an object")
        date_text = str(entry.get("date", "")).strip()
        if date_text not in barrier2["dates"]:
            raise ValueError(f"unexpected date in schedule: {date_text}")
        area_ids = entry.get("area_ids")
        if not isinstance(area_ids, dict):
            raise ValueError("schedule entry area_ids must be an object")
        normalized_area_ids: Dict[str, List[int]] = {}
        for area_name, ids in area_ids.items():
            normalized_area_ids[str(area_name).strip()] = _ensure_list_of_ints(ids, valid_ids)
        normalized.append(
            {
                "date": date_text,
                "area_ids": normalized_area_ids,
                "note": str(entry.get("note", "") or "").strip(),
            }
        )
    return {"schedule": normalized}


def validate_final_schedule(schedule: List[Dict[str, Any]], barrier2: Dict[str, Any]) -> None:
    template = barrier2["template"]
    final_pool = set(barrier2["final_pool"])

    if len(schedule) != len(barrier2["dates"]):
        raise ValueError("schedule date count mismatch")

    seen_dates = set()
    assigned_ids_all: List[int] = []
    schedule_by_person: Dict[int, List[datetime.date]] = {}
    for entry in schedule:
        date_text = entry["date"]
        if date_text in seen_dates:
            raise ValueError(f"duplicate schedule date: {date_text}")
        seen_dates.add(date_text)
        expected_areas = template[date_text]
        area_ids = entry["area_ids"]
        if set(area_ids.keys()) != set(expected_areas.keys()):
            raise ValueError(f"area set mismatch on {date_text}")
        per_day_ids: List[int] = []
        for area_name, expected_count in expected_areas.items():
            assigned_ids = area_ids.get(area_name, [])
            if len(assigned_ids) != expected_count:
                raise ValueError(f"slot count mismatch on {date_text}/{area_name}")
            per_day_ids.extend(assigned_ids)
            assigned_ids_all.extend(assigned_ids)
        if len(per_day_ids) != len(set(per_day_ids)):
            raise ValueError(f"same person assigned twice on {date_text}")
        parsed_date = datetime.strptime(date_text, "%Y-%m-%d").date()
        for person_id in per_day_ids:
            schedule_by_person.setdefault(person_id, []).append(parsed_date)

    if set(assigned_ids_all) != final_pool or len(assigned_ids_all) != len(final_pool):
        raise ValueError("final assigned set does not match final_pool")

    for rule in barrier2["supported_rules"]:
        rule_type = rule["type"]
        if rule_type == "fixed_area":
            person_id = rule["person_id"]
            required_area = rule["area"]
            for entry in schedule:
                for area_name, ids in entry["area_ids"].items():
                    if person_id in ids and area_name != required_area:
                        raise ValueError(f"fixed_area violated for {person_id}")
        elif rule_type == "avoid_same_day_pair":
            person_a = rule["person_a"]
            person_b = rule["person_b"]
            for entry in schedule:
                day_ids = {item for ids in entry["area_ids"].values() for item in ids}
                if person_a in day_ids and person_b in day_ids:
                    raise ValueError(f"avoid_same_day_pair violated for {person_a}/{person_b}")
        elif rule_type == "bind_same_day":
            person_a = rule["person_a"]
            person_b = rule["person_b"]
            day_a = None
            day_b = None
            for entry in schedule:
                day_ids = {item for ids in entry["area_ids"].values() for item in ids}
                if person_a in day_ids:
                    day_a = entry["date"]
                if person_b in day_ids:
                    day_b = entry["date"]
            if day_a != day_b:
                raise ValueError(f"bind_same_day violated for {person_a}/{person_b}")
        elif rule_type == "avoid_consecutive_days":
            person_id = rule["person_id"]
            assigned_days = sorted(schedule_by_person.get(person_id, []))
            for idx in range(1, len(assigned_days)):
                if assigned_days[idx] - assigned_days[idx - 1] == timedelta(days=1):
                    raise ValueError(f"avoid_consecutive_days violated for {person_id}")


def fallback_fill_schedule(barrier2: Dict[str, Any]) -> List[Dict[str, Any]]:
    rules = barrier2["supported_rules"]
    if any(rule["type"] not in {"fixed_area", "avoid_same_day_pair", "bind_same_day"} for rule in rules):
        raise ValueError("safe fallback does not support the current rule set")

    slots = list(barrier2["slots"])
    final_pool = list(barrier2["final_pool"])
    template = barrier2["template"]
    schedule_map: Dict[str, Dict[str, List[int]]] = {
        date_text: {area_name: [] for area_name in areas.keys()}
        for date_text, areas in template.items()
    }

    fixed_area_map = {rule["person_id"]: rule["area"] for rule in rules if rule["type"] == "fixed_area"}
    bind_pairs = [(rule["person_a"], rule["person_b"]) for rule in rules if rule["type"] == "bind_same_day"]
    avoid_pairs = [(rule["person_a"], rule["person_b"]) for rule in rules if rule["type"] == "avoid_same_day_pair"]

    placed: set[int] = set()
    remaining_slots = list(slots)

    def _place(person_id: int, required_date: str | None = None) -> bool:
        required_area = fixed_area_map.get(person_id)
        for slot in list(remaining_slots):
            if required_date and slot["date"] != required_date:
                continue
            if required_area and slot["area"] != required_area:
                continue
            day_ids = {
                assigned
                for ids in schedule_map[slot["date"]].values()
                for assigned in ids
            }
            blocked = False
            for person_a, person_b in avoid_pairs:
                if person_id == person_a and person_b in day_ids:
                    blocked = True
                if person_id == person_b and person_a in day_ids:
                    blocked = True
            if blocked:
                continue
            schedule_map[slot["date"]][slot["area"]].append(person_id)
            remaining_slots.remove(slot)
            placed.add(person_id)
            return True
        return False

    for person_a, person_b in bind_pairs:
        if person_a in placed or person_b in placed:
            continue
        for date_text in barrier2["dates"]:
            if _place(person_a, required_date=date_text) and _place(person_b, required_date=date_text):
                break
        else:
            raise ValueError("fallback could not satisfy bind_same_day rule")

    for person_id in final_pool:
        if person_id in placed:
            continue
        if not _place(person_id):
            raise ValueError(f"fallback could not place person {person_id}")

    schedule: List[Dict[str, Any]] = []
    for date_text in barrier2["dates"]:
        schedule.append(
            {
                "date": date_text,
                "area_ids": schedule_map[date_text],
                "note": "fallback_fill",
            }
        )
    validate_final_schedule(schedule, barrier2)
    return schedule
