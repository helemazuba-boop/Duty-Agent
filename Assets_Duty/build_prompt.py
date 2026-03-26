from __future__ import annotations

from datetime import date, datetime
from typing import Dict, List

from prompt_config import KEYWORD_REGISTRY, PROMPTS


def is_module_active(module_name: str, instruction: str, data_present: bool) -> bool:
    instruction_lower = instruction.lower()
    keywords = KEYWORD_REGISTRY.get(module_name, [])
    return data_present or any(keyword in instruction_lower for keyword in keywords)


def _format_ids(values: List[int]) -> str:
    return " ".join(str(person_id) for person_id in values)


def _format_count_map(counts: Dict[int, int]) -> str:
    if not counts:
        return ""
    parts: List[str] = []
    for person_id in sorted(counts.keys()):
        count = int(counts.get(person_id, 0) or 0)
        if count <= 0:
            continue
        parts.append(f"{person_id}*{count}" if count > 1 else str(person_id))
    return " ".join(parts)


def _parse_iso_date(value: str) -> date | None:
    try:
        return datetime.strptime(str(value or "").strip(), "%Y-%m-%d").date()
    except ValueError:
        return None


def _build_boundary_hints(start_date_text: str) -> str:
    start = _parse_iso_date(start_date_text)
    if start is None:
        return "boundary_dates=12-31->01-01"

    hints: List[str] = []
    next_month = date(start.year, start.month, 1)
    for _ in range(16):
        if next_month.month == 12:
            month_end = date(next_month.year, 12, 31)
            following = date(next_month.year + 1, 1, 1)
        else:
            following = date(next_month.year, next_month.month + 1, 1)
            month_end = following.fromordinal(following.toordinal() - 1)
        if month_end >= start:
            hints.append(f"{month_end:%m-%d}->{following:%m-%d}")
        if len(hints) >= 3:
            break
        if next_month.month == 12:
            next_month = date(next_month.year + 1, 1, 1)
        else:
            next_month = date(next_month.year, next_month.month + 1, 1)

    if "12-31->01-01" not in hints:
        hints.append("12-31->01-01")
    return "boundary_dates=" + ", ".join(dict.fromkeys(hints))


def build_prompt_messages(
    all_ids: List[int],
    current_time: str,
    id_to_active: Dict[int, int],
    instruction: str,
    duty_rule: str,
    area_names: List[str],
    area_per_day_counts: Dict[str, int],
    debt_counts: Dict[int, int],
    credit_counts: Dict[int, int],
    start_date: str,
    previous_context: str = "",
    model_profile: str = "auto",
    orchestration_mode: str = "auto",
    single_pass_strategy: str = "cloud_standard",
    last_pointer: int = 0,
) -> List[Dict[str, str]]:
    del area_names
    del area_per_day_counts
    del previous_context

    inactive_ids = [person_id for person_id, active in id_to_active.items() if active == 0]
    compact_mode = (
        single_pass_strategy != "incremental_thinking"
        and (model_profile == "campus_small" or orchestration_mode == "multi_agent")
    )

    params_list = [
        f"all_roster_ids={_format_ids(all_ids)}",
        f"current_time={current_time}",
        f"start_date={start_date}",
        _build_boundary_hints(start_date),
        f"last_pointer={max(0, int(last_pointer or 0))}",
        f"user_instruction={instruction}",
    ]

    methods_list: List[str] = []

    if is_module_active("debt", instruction, bool(debt_counts)):
        params_list.append(PROMPTS["param_debt"].format(debt_counts=_format_count_map(debt_counts)))
        methods_list.append(PROMPTS["rule_debt"])

    if is_module_active("credit", instruction, bool(credit_counts)):
        params_list.append(PROMPTS["param_credit"].format(credit_counts=_format_count_map(credit_counts)))
        methods_list.append(PROMPTS["rule_credit"])

    if is_module_active("inactive", instruction, bool(inactive_ids)):
        params_list.append(PROMPTS["param_inactive"].format(inactive_ids=_format_ids(inactive_ids)))
        methods_list.append(PROMPTS["rule_inactive"])

    if is_module_active("multi_day", instruction, False):
        methods_list.append(PROMPTS["rule_multi_day"])

    duty_rule = str(duty_rule or "").strip()
    if duty_rule:
        methods_list.append(f"user_defined_rule={duty_rule}")

    methods_list.append(
        "Formatting constraints: declare every alias in [areas] before using it in [schedule]. "
        "Use MM-DD dates only. Keep aliases short uppercase tokens such as A, B, S. "
        "Use `Alias = AreaName` in [areas]. "
        "Use `MM-DD = A:1001 1002 | B:1003 1004` in [schedule]. "
        "Use spaces between IDs and `|` only between area assignments. "
        "If a date needs a note, append it as a trailing `# ...` comment on that schedule line. "
        "Use `debt = ...` and `credit = ...` lines in [state]. "
        "Same-day cross-area reuse is allowed. Same-area duplicate IDs are forbidden."
    )

    if single_pass_strategy == "incremental_thinking":
        methods_list.append(
            "Work through the dates in order internally, then output only the final V2 result."
        )

    dynamic_parameters = "\n".join(params_list)
    dynamic_methods = "\n".join(f"- {item}" for item in methods_list)

    template_key = "compact_base" if compact_mode else "regular_system_base"
    system_content = PROMPTS[template_key].format(
        dynamic_parameters=dynamic_parameters,
        dynamic_methods=dynamic_methods,
    )
    return [{"role": "user", "content": system_content}]
