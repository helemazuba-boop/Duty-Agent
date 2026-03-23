# build_prompt.py
from typing import Dict, List

from prompt_config import KEYWORD_REGISTRY, PROMPTS
from state_ops import DEFAULT_SINGLE_AREA_NAME

def is_module_active(module_name: str, instruction: str, data_present: bool) -> bool:
    """Determine if a functional module should be injected into the prompt."""
    instruction_lower = instruction.lower()
    
    # Check if keyword registry contains the module
    keywords = KEYWORD_REGISTRY.get(module_name, [])
    keyword_match = any(kw in instruction_lower for kw in keywords)
    
    # Rule: Active if data is present OR user mentioned it via keyword
    return data_present or keyword_match

def build_prompt_messages(
    all_ids: List[int],
    current_time: str,
    id_to_active: Dict[int, int],
    instruction: str,
    duty_rule: str,
    area_names: List[str],
    area_per_day_counts: Dict[str, int],
    debt_list: List[int],
    credit_list: List[int],
    previous_context: str = "",
    model_profile: str = "auto",
    orchestration_mode: str = "auto",
    single_pass_strategy: str = "cloud_standard",
) -> List[Dict[str, str]]:
    """Build prompt messages for the unified scheduling engine."""

    inactive_ids = [pid for pid, active in id_to_active.items() if active == 0]
    compact_mode = (
        single_pass_strategy != "incremental_thinking"
        and (model_profile == "campus_small" or orchestration_mode == "multi_agent")
    )

    params_list = [
        f"<all_roster_ids>{','.join(map(str, all_ids))}</all_roster_ids>",
        f"<current_time>{current_time}</current_time>",
        f"<user_instruction>{instruction}</user_instruction>",
        f"<single_pass_strategy>{single_pass_strategy}</single_pass_strategy>",
    ]
    if previous_context:
        params_list.append(f"<previous_run_memory>{previous_context}</previous_run_memory>")

    methods_list = []

    # Debt Logic
    if is_module_active("debt", instruction, bool(debt_list)):
        params_list.append(PROMPTS["param_debt"].format(debt_list=','.join(map(str, debt_list))))
        methods_list.append(PROMPTS["rule_debt"])

    # Credit Logic
    if is_module_active("credit", instruction, bool(credit_list)):
        params_list.append(PROMPTS["param_credit"].format(credit_list=','.join(map(str, credit_list))))
        methods_list.append(PROMPTS["rule_credit"])

    # Inactive Logic
    if is_module_active("inactive", instruction, bool(inactive_ids)):
        params_list.append(PROMPTS["param_inactive"].format(inactive_ids=','.join(map(str, inactive_ids))))
        methods_list.append(PROMPTS["rule_inactive"])

    if is_module_active("multi_day", instruction, False):
        methods_list.append(PROMPTS["rule_multi_day"])

    duty_rule = (duty_rule or "").strip()
    if duty_rule:
        methods_list.append(f"<user_defined_rule>\n{duty_rule}\n</user_defined_rule>")

    methods_list.append(
        "<output_guard>\n"
        "Return CSV only.\n"
        "The header must be Date first and Note last. Every column between them is an area name.\n"
        f"If the user did not explicitly define areas, use a single area column named {DEFAULT_SINGLE_AREA_NAME}.\n"
        "Use the same area columns for every data row.\n"
        "Each area cell must contain only the scheduled IDs for that date and area.\n"
        "When an area cell contains multiple IDs, quote that cell and separate IDs with commas, for example \"1,2\".\n"
        "Leave an area cell empty only when that area truly has no assignment for that date.\n"
        "The same ID may appear in different area columns on the same date, and may also appear on different dates.\n"
        "Within one area cell, do not repeat the same ID.\n"
        "</output_guard>"
    )

    if single_pass_strategy == "incremental_thinking":
        methods_list.append(
            "<execution_hint>\nThink through the constraints carefully before finalizing the schedule, "
            "but only return the final result.\n</execution_hint>"
        )

    dynamic_parameters = "\n".join(params_list)
    dynamic_methods = "\n".join(methods_list) if methods_list else "<!-- No specific processing rules triggered. Follow basic sequence. -->"

    if compact_mode:
        system_content = PROMPTS["compact_base"].format(
            dynamic_parameters=dynamic_parameters,
            dynamic_methods=dynamic_methods,
        )
    else:
        system_content = PROMPTS["regular_system_base"].format(
            dynamic_parameters=dynamic_parameters,
            dynamic_methods=dynamic_methods,
        )

    return [{"role": "user", "content": system_content}]
