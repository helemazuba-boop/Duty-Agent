# build_prompt.py
from typing import List, Dict, Any
from prompt_config import PROMPTS, KEYWORD_REGISTRY

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
    debt_list: List[int],
    credit_list: List[int],
    previous_context: str = "",
    prompt_mode: str = "Regular",
) -> List[Dict[str, str]]:
    """Build the XML-based prompt messages for the LLM."""

    inactive_ids = [pid for pid, active in id_to_active.items() if active == 0]

    if prompt_mode.lower() == "incremental":
        # Incremental mode: Minimal parameters
        params = [
            f"<all_roster_ids>{','.join(map(str, all_ids))}</all_roster_ids>",
            f"<inactive_ids>{','.join(map(str, inactive_ids))}</inactive_ids>",
            f"<current_time>{current_time}</current_time>",
            f"<user_instruction>{instruction}</user_instruction>"
        ]
        dynamic_parameters = "\n".join(params)
        system_content = PROMPTS["incremental_base"].format(dynamic_parameters=dynamic_parameters)
        return [{"role": "user", "content": system_content}]

    # Regular Mode: Dynamic XML Assembly
    # 1. Standard Parameters (Always Injected)
    params_list = [
        f"<all_roster_ids>{','.join(map(str, all_ids))}</all_roster_ids>",
        f"<current_time>{current_time}</current_time>",
        f"<user_instruction>{instruction}</user_instruction>",
    ]
    if previous_context:
        params_list.append(f"<previous_run_memory>{previous_context}</previous_run_memory>")

    # 2. Conditional Parameters & Methods
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
        
    # Multi-day Logic (No specific params yet, just rule)
    if is_module_active("multi_day", instruction, False):
        methods_list.append(PROMPTS["rule_multi_day"])

    # User defined custom rules
    duty_rule = (duty_rule or "").strip()
    if duty_rule:
        methods_list.append(f"<user_defined_rule>\n{duty_rule}\n</user_defined_rule>")

    # Assembly
    dynamic_parameters = "\n".join(params_list)
    dynamic_methods = "\n".join(methods_list) if methods_list else "<!-- No specific processing rules triggered. Follow basic sequence. -->"

    system_content = PROMPTS["regular_system_base"].format(
        dynamic_parameters=dynamic_parameters,
        dynamic_methods=dynamic_methods
    )

    return [{"role": "user", "content": system_content}]
