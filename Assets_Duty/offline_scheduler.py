# -*- coding: utf-8 -*-
"""Deterministic, model-free schedule generator (the ``offline`` orchestration mode).

The offline executor produces a duty schedule with a pure rotation algorithm (no
LLM call), then hands a V2 INI completion to the existing single_pass settle
pipeline (:func:`single_pass_executor.apply_single_pass_completion`). Reusing the
settle half keeps debt/credit/pointer reconciliation and atomic persistence
identical to model-backed runs, so an offline run stays fully consistent with the
LLM path.

Design choices (see the approved plan):

* **Structure is learned, not configured.** Which areas exist and how many people
  each needs per day are read from the most recent ``schedule_pool`` entry; with
  no history it falls back to a single default area (``DEFAULT_SINGLE_AREA_NAME``
  x ``DEFAULT_ASSIGNMENTS_PER_AREA``).
* **Fairness bookkeeping is delegated.** The generator emits only ``[areas]`` and
  ``[schedule]`` (no ``[state]``). ``apply_single_pass_completion`` then derives
  the pointer + consumed-credit through :func:`estimate_pointer_progress` -- the
  same path used when a model omits the state block -- so debt/credit accounting
  is never re-implemented here.
* **The generator is fairness-aware in its choices.** It assigns debtors first
  (bounded by their outstanding debt) and skips credit holders during normal
  rotation so the settle side consumes their credit; a fallback pass includes
  everyone if too few non-credit people are available to staff a day.

All schedule-building helpers are pure (no I/O, no clock access) for testability;
only :func:`run_offline_schedule` touches config/roster/state.
"""

from __future__ import annotations

from datetime import date, datetime, timedelta
from typing import Dict, List, Optional, Sequence, Tuple

from execution_profiles import ExecutionPlan
from single_pass_executor import apply_single_pass_completion
from state_ops import (
    Context,
    DEFAULT_ASSIGNMENTS_PER_AREA,
    DEFAULT_SINGLE_AREA_NAME,
    clone_count_map,
    load_config,
    load_roster,
    load_state,
)

OFFLINE_DEFAULT_SCHEDULE_DAYS = 7
AI_RESPONSE_MAX_CHARS = 20000


def _parse_iso_date(value: object) -> Optional[date]:
    text = str(value or "").strip()
    if not text:
        return None
    try:
        return date.fromisoformat(text[:10])
    except ValueError:
        return None


def build_target_dates(start_date: date, num_days: int, skip_weekends: bool) -> List[date]:
    """Return the next ``num_days`` schedule dates from ``start_date`` (inclusive),
    skipping Sat/Sun when ``skip_weekends`` is set."""
    dates: List[date] = []
    if num_days <= 0:
        return dates
    max_iterations = num_days * 3 + 14  # generous bound so weekend-skipping terminates
    cursor = start_date
    for _ in range(max_iterations):
        if len(dates) >= num_days:
            break
        if not (skip_weekends and cursor.weekday() >= 5):
            dates.append(cursor)
        cursor += timedelta(days=1)
    return dates


def learn_area_structure(schedule_pool: Sequence[dict]) -> List[Tuple[str, int]]:
    """Learn ``[(area_name, per_day_headcount), ...]`` from the most recent pool
    entry. Falls back to a single default area when there is no usable history."""
    latest: Optional[dict] = None
    latest_date: Optional[date] = None
    for entry in schedule_pool or []:
        if not isinstance(entry, dict):
            continue
        entry_date = _parse_iso_date(entry.get("date"))
        if entry_date is None:
            continue
        if latest_date is None or entry_date > latest_date:
            latest_date = entry_date
            latest = entry

    structure: List[Tuple[str, int]] = []
    if isinstance(latest, dict):
        assignments = latest.get("area_assignments")
        if isinstance(assignments, dict):
            for raw_name, students in assignments.items():
                # Collapse whitespace/newlines so the name is a safe single-line
                # token for the [areas] section; drop comment-leading names.
                name = " ".join(str(raw_name or "").split())
                if not name or name.startswith("#") or name.startswith(";"):
                    continue
                count = len(students) if isinstance(students, list) else 0
                if count > 0:
                    structure.append((name, count))

    if not structure:
        structure = [(DEFAULT_SINGLE_AREA_NAME, DEFAULT_ASSIGNMENTS_PER_AREA)]
    return structure


def build_active_rotation(
    all_ids: Sequence[int],
    id_to_active: Dict[int, int],
    last_pointer: int,
) -> List[int]:
    """Active person IDs in ring order starting at ``last_pointer`` (index into the
    full roster order), matching the traversal ``estimate_pointer_progress`` uses."""
    total = len(all_ids)
    if total == 0:
        return []
    start = max(0, min(int(last_pointer or 0), total - 1))
    rotation: List[int] = []
    for offset in range(total):
        person_id = int(all_ids[(start + offset) % total])
        if id_to_active.get(person_id, 1) != 0:
            rotation.append(person_id)
    return rotation


def plan_daily_assignments(
    rotation: List[int],
    debtor_budget: Dict[int, int],
    credit_ids: set,
    slots_per_day: int,
    num_days: int,
) -> List[List[int]]:
    """Pick ``slots_per_day`` distinct people for each of ``num_days`` days.

    Priority order per day: (1) debtors, in ring order, each once/day and bounded
    by their remaining debt budget; (2) normal forward rotation, skipping credit
    holders; (3) fallback that includes anyone remaining so a day is never left
    under-staffed. The rotation cursor persists across days for round-robin
    fairness; debtor and fallback picks do not advance it.
    """
    if not rotation or slots_per_day <= 0 or num_days <= 0:
        return []
    ring_len = len(rotation)
    remaining_debt = dict(debtor_budget)
    cursor = 0
    days: List[List[int]] = []

    for _ in range(num_days):
        used: set = set()
        picks: List[int] = []

        # 1) Debtors first (bounded by outstanding debt).
        for offset in range(ring_len):
            if len(picks) >= slots_per_day:
                break
            person_id = rotation[(cursor + offset) % ring_len]
            if person_id in used:
                continue
            if remaining_debt.get(person_id, 0) > 0:
                picks.append(person_id)
                used.add(person_id)
                remaining_debt[person_id] -= 1

        # 2) Normal forward rotation, skipping credit holders.
        steps = 0
        while len(picks) < slots_per_day and steps < ring_len:
            person_id = rotation[cursor]
            cursor = (cursor + 1) % ring_len
            steps += 1
            if person_id in used or person_id in credit_ids:
                continue
            picks.append(person_id)
            used.add(person_id)

        # 3) Fallback: too few non-credit people -> staff with whoever remains.
        if len(picks) < slots_per_day:
            for offset in range(ring_len):
                if len(picks) >= slots_per_day:
                    break
                person_id = rotation[(cursor + offset) % ring_len]
                if person_id in used:
                    continue
                picks.append(person_id)
                used.add(person_id)

        days.append(picks)
    return days


def build_ini_completion(
    area_structure: List[Tuple[str, int]],
    daily_ids: List[List[int]],
    target_dates: List[date],
) -> str:
    """Render a V2 INI completion (``[areas]`` + ``[schedule]``) consumable by
    :func:`llm_transport.parse_schedule_completion`. The ``[state]`` block is
    intentionally omitted so the settle side derives pointer/credit itself."""
    aliases = [f"A{index}" for index in range(len(area_structure))]

    lines: List[str] = ["[areas]"]
    for (area_name, _count), alias in zip(area_structure, aliases):
        lines.append(f"{alias} = {area_name}")

    lines.append("")
    lines.append("[schedule]")
    for day_date, ids in zip(target_dates, daily_ids):
        segments: List[str] = []
        position = 0
        for (_area_name, count), alias in zip(area_structure, aliases):
            area_slice = ids[position:position + count]
            position += count
            if area_slice:
                segments.append(f"{alias}:" + " ".join(str(person_id) for person_id in area_slice))
        if segments:
            lines.append(f"{day_date.strftime('%m-%d')} = " + " | ".join(segments))

    return "\n".join(lines)


def _summarize(
    area_structure: List[Tuple[str, int]],
    target_dates: List[date],
) -> str:
    if not target_dates:
        return "离线算法未生成任何排班。"
    span = f"{target_dates[0].isoformat()} 至 {target_dates[-1].isoformat()}"
    area_desc = "、".join(f"{name}({count}人)" for name, count in area_structure)
    per_day = sum(count for _name, count in area_structure)
    return (
        f"离线算法已生成 {len(target_dates)} 天值日安排（{span}），"
        f"区域：{area_desc}，共 {per_day} 人/天。"
    )


def run_offline_schedule(
    ctx: Context,
    input_data: dict,
    execution_plan: ExecutionPlan,
    emit_progress_fn=None,
    stop_event=None,
) -> dict:
    """Deterministic offline run: learn structure, rotate fairly, then settle via
    the shared single_pass pipeline. No model/network is touched."""
    if stop_event and stop_event.is_set():
        raise InterruptedError("Cancelled.")

    input_data = dict(input_data or {})
    trace_id = str(input_data.get("trace_id", "")).strip()

    config = load_config(ctx)
    name_to_id, id_to_name, all_ids, id_to_active = load_roster(ctx.paths["roster"])
    state_data = load_state(ctx.paths["state"])

    active_ids = [person_id for person_id in all_ids if id_to_active.get(person_id, 1) != 0]
    if not active_ids:
        return {
            "status": "error",
            "message": "离线排班需要至少一名在册在岗成员，请先在花名册中添加或启用成员。",
            "trace_id": trace_id,
            "selected_executor": "offline",
        }

    num_days = int(config.get("offline_schedule_days", OFFLINE_DEFAULT_SCHEDULE_DAYS) or OFFLINE_DEFAULT_SCHEDULE_DAYS)
    skip_weekends = bool(config.get("offline_skip_weekends", True))

    start_date = datetime.now().date()
    target_dates = build_target_dates(start_date, num_days, skip_weekends)
    if not target_dates:
        return {
            "status": "error",
            "message": "离线排班未能确定任何排班日期（请检查排班天数设置）。",
            "trace_id": trace_id,
            "selected_executor": "offline",
        }

    area_structure = learn_area_structure(state_data.get("schedule_pool", []))
    slots_per_day = sum(count for _name, count in area_structure)

    active_set = set(active_ids)
    debt_counts = clone_count_map(state_data.get("debt_counts", {}), set(all_ids))
    credit_counts = clone_count_map(state_data.get("credit_counts", {}), set(all_ids))
    debtor_budget = {
        person_id: int(count)
        for person_id, count in debt_counts.items()
        if person_id in active_set and int(count) > 0
    }
    credit_ids = {
        person_id
        for person_id, count in credit_counts.items()
        if person_id in active_set and int(count) > 0
    }

    last_pointer = int(state_data.get("last_pointer", 0) or 0)
    rotation = build_active_rotation(all_ids, id_to_active, last_pointer)

    if emit_progress_fn:
        emit_progress_fn(
            "planning",
            f"离线算法排班：{len(target_dates)} 天 / {slots_per_day} 人每天。",
            "",
        )

    daily_ids = plan_daily_assignments(rotation, debtor_budget, credit_ids, slots_per_day, len(target_dates))
    ini_text = build_ini_completion(area_structure, daily_ids, target_dates)

    resume_context = {
        "mode": "offline",
        "start_date": start_date.isoformat(),
        "trace_id": trace_id,
        "prompt_metadata": {},
        "execution_plan_meta": execution_plan.to_metadata() if execution_plan is not None else {},
        "single_pass_strategy": None,
        "transport_overrides": {},
    }

    result = apply_single_pass_completion(ctx, ini_text, resume_context, stop_event=stop_event)
    result["selected_executor"] = "offline"
    result["ai_response"] = _summarize(area_structure, target_dates)[:AI_RESPONSE_MAX_CHARS]
    return result
