from __future__ import annotations

import json
from typing import Any, Dict, List

from state_ops import DEFAULT_SINGLE_AREA_NAME


def _render_input(payload: Dict[str, Any]) -> str:
    return json.dumps(payload, ensure_ascii=False, indent=2)


def _json_only_prompt(role: str, task: str, payload: Dict[str, Any], output_contract: Dict[str, Any], extra_rules: List[str]) -> List[Dict[str, str]]:
    rules_text = "\n".join(f"- {item}" for item in extra_rules)
    content = (
        f"You are {role} in the Duty-Agent scheduling pipeline.\n"
        "Return ONLY one valid JSON object. No markdown fences. No extra narration.\n\n"
        f"Task:\n{task}\n\n"
        f"Input JSON:\n{_render_input(payload)}\n\n"
        f"Output contract example:\n{_render_input(output_contract)}\n\n"
        f"Rules:\n{rules_text}\n"
    )
    return [{"role": "user", "content": content}]


def build_agent_prompt_messages(agent_id: str, agent_context: Dict[str, Any]) -> List[Dict[str, str]]:
    builders = {
        "agent1_anchor": _build_agent1_prompt,
        "agent2_accountant": _build_agent2_prompt,
        "agent3_rule": _build_agent3_prompt,
        "agent4_priority": _build_agent4_prompt,
        "agent5_pointer": _build_agent5_prompt,
        "agent6_assembly": _build_agent6_prompt,
    }
    try:
        return builders[agent_id](agent_context)
    except KeyError as ex:
        raise ValueError(f"Unsupported agent prompt: {agent_id}") from ex


def _build_agent1_prompt(agent_context: Dict[str, Any]) -> List[Dict[str, str]]:
    payload = {
        "request_time": agent_context["request_time"],
        "instruction": agent_context["instruction"],
        "previous_note": agent_context["previous_note"],
        "duty_rule": agent_context["duty_rule"],
    }
    output_contract = {
        "dates": ["2026-03-16", "2026-03-17"],
        "template": {
            "2026-03-16": {"教室": 2, "清洁区": 2},
            "2026-03-17": {"教室": 2, "清洁区": 2},
        },
        "total_slots": 8,
    }
    rules = [
        "Dates must be YYYY-MM-DD and sorted ascending.",
        "Do not create dates before request_time.",
        "Template keys must exactly match dates.",
        "Counts must be positive integers.",
        f"If the user does not explicitly define areas, use one area named {DEFAULT_SINGLE_AREA_NAME}.",
    ]
    return _json_only_prompt("Agent1 Anchor Planner", "Extract dates and the full area template for each date.", payload, output_contract, rules)


def _build_agent2_prompt(agent_context: Dict[str, Any]) -> List[Dict[str, str]]:
    payload = {
        "instruction": agent_context["instruction"],
        "request_time": agent_context["request_time"],
        "roster_ids": agent_context["all_ids"],
        "current_debt_ids": agent_context["debt_list"],
        "current_credit_ids": agent_context["credit_list"],
    }
    output_contract = {
        "absent_ids": [5],
        "new_debt_ids": [5],
        "new_credit_ids": [8],
        "must_run_ids": [8],
        "volunteer_ids": [8],
        "warnings": [],
    }
    rules = [
        "Only output IDs present in roster_ids.",
        "Absences should not be duplicated in new_debt_ids if already owed.",
        "must_run_ids are for people explicitly required this round.",
    ]
    return _json_only_prompt("Agent2 Ledger Intent Extractor", "Extract absence, volunteer, debt, credit and must-run intent.", payload, output_contract, rules)


def _build_agent3_prompt(agent_context: Dict[str, Any]) -> List[Dict[str, str]]:
    payload = {
        "instruction": agent_context["instruction"],
        "request_time": agent_context["request_time"],
        "roster_ids": agent_context["all_ids"],
        "duty_rule": agent_context["duty_rule"],
    }
    output_contract = {
        "supported_rules": [
            {"type": "fixed_area", "person_id": 3, "area": DEFAULT_SINGLE_AREA_NAME},
            {"type": "avoid_same_day_pair", "person_a": 5, "person_b": 8},
        ],
        "unsupported_rules": [],
        "warnings": [],
    }
    rules = [
        "Only output supported rule types: fixed_area, avoid_same_day_pair, bind_same_day, avoid_consecutive_days.",
        "Unsupported rules must go to unsupported_rules instead of supported_rules.",
        "All person IDs must come from roster_ids.",
    ]
    return _json_only_prompt("Agent3 Rule Extractor", "Extract supported structured scheduling rules.", payload, output_contract, rules)


def _build_agent4_prompt(agent_context: Dict[str, Any]) -> List[Dict[str, str]]:
    payload = {
        "dates": agent_context["dates"],
        "total_slots": agent_context["total_slots"],
        "active_ids": agent_context["active_ids"],
        "absent_ids": agent_context["absent_ids"],
        "debt_list": agent_context["debt_list"],
        "new_debt_ids": agent_context["new_debt_ids"],
        "must_run_ids": agent_context["must_run_ids"],
        "volunteer_ids": agent_context["volunteer_ids"],
    }
    output_contract = {
        "priority_pool": [8, 12],
        "notes": ["Debt first, explicit must-run kept."],
    }
    rules = [
        "priority_pool must contain unique IDs only.",
        "Do not include absent or inactive people.",
        "Count must not exceed total_slots.",
        "Prefer explicit must-run and unresolved debt first.",
    ]
    return _json_only_prompt("Agent4 Priority Pool Planner", "Build the high-priority pool for this round.", payload, output_contract, rules)


def _build_agent5_prompt(agent_context: Dict[str, Any]) -> List[Dict[str, str]]:
    payload = {
        "dates": agent_context["dates"],
        "total_slots": agent_context["total_slots"],
        "all_ids": agent_context["all_ids"],
        "active_ids": agent_context["active_ids"],
        "inactive_ids": agent_context["inactive_ids"],
        "absent_ids": agent_context["absent_ids"],
        "credit_list": agent_context["credit_list"],
        "last_pointer": agent_context["last_pointer"],
    }
    output_contract = {
        "pointer_pool": [15, 16],
        "pointer_after": 17,
        "consumed_credit_ids": [14],
        "notes": ["Skipped one credited student when pointer reached them."],
    }
    rules = [
        "pointer_pool must contain unique IDs only.",
        "Do not include absent or inactive IDs.",
        "Generate enough ordered pointer candidates for Barrier2 to fill the remaining slots after overlap removal.",
        "pointer_after must be the next roster index after the final inspected candidate.",
    ]
    return _json_only_prompt("Agent5 Pointer Pool Planner", "Advance pointer and fill the remaining candidate pool.", payload, output_contract, rules)


def _build_agent6_prompt(agent_context: Dict[str, Any]) -> List[Dict[str, str]]:
    payload = {
        "dates": agent_context["dates"],
        "template": agent_context["template"],
        "final_pool": agent_context["final_pool"],
        "supported_rules": agent_context["supported_rules"],
    }
    output_contract = {
        "schedule": [
            {
                "date": "2026-03-16",
                "area_ids": {"教室": [8, 12], "清洁区": [8, 15]},
                "note": "",
            }
        ]
    }
    rules = [
        "Use only IDs from final_pool.",
        "Fill every required slot from template.",
        "Do not invent extra dates or areas.",
        "The same person may appear on different dates, and may also appear in different areas on the same date.",
        "Do not repeat the same person twice inside one area list for the same date.",
    ]
    return _json_only_prompt("Agent6 Schedule Assembler", "Assign final_pool IDs into template slots.", payload, output_contract, rules)
