# prompt_config.py

# --- 关键字雷达 (Keyword Radar) ---
# 定义嗅探逻辑：当指令或上下文中出现这些词时，激活对应的 XML 模块
KEYWORD_REGISTRY = {
    "debt": ["欠", "补", "优先", "之前", "debt", "惩罚"],
    "credit": ["积", "连", "表扬", "休息", "抵扣", "credit", "奖励"],
    "inactive": ["假", "除开", "不要", "生病", "屏蔽", "inactive", "请假"],
    "multi_day": ["到", "几天", "至", "连排", "整周"],
}

# --- 模块化 Prompt (纯 XML 模板) ---
PROMPTS = {
    "regular_system_base": """<system_directive>
<role>Duty-Agent</role>
<task>Schedule balancing Hard(Sick/Inactive) & Soft Constraints + Fairness(Debt)</task>
<process_guideline>Process the information below in turns.</process_guideline>

<context_parameters>
{dynamic_parameters}
</context_parameters>

<processing_steps>
{dynamic_methods}
</processing_steps>

<output_schema>
<directive>Output ONLY the final schedule inside <csv> tags. Do not output any thinking process outside the tags.</directive>
<columns>Date,Assigned_IDs,Note</columns>
</output_schema>

<recovery_mechanism>
If you realize you made a logical error mid-generation, DO NOT apologize or explain in natural language. 
Simply type the word "RESET" on a new line, and restart the entire CSV output from the beginning.
</recovery_mechanism>
</system_directive>
""",
    
    # 增量模式（小模型专用）- 也更新为 XML 风格以保持一致性
    "incremental_base": """<system_directive>
<role>Duty-Agent (Lite)</role>
<task>Generate a basic duty schedule purely based on roster sequence.</task>

<context_parameters>
{dynamic_parameters}
</context_parameters>

<output_schema>
<directive>Output ONLY the final schedule inside <csv> tags.</directive>
<columns>Date,Assigned_IDs,Note</columns>
</output_schema>
</system_directive>
""",
    
# ---------------- 动态规则块 (Methods / Rules) ----------------

    "rule_debt": """<rule_debt>
[PRIORITY HIGH - BACKFILL] 
Check the <current_debt_list>. If anyone in this list is available, you MUST schedule them FIRST to clear their debt before continuing the normal sequence.
</rule_debt>""",

    "param_debt": "<current_debt_list>{debt_list}</current_debt_list>",
    
    # --------------------------------------------------------------

    "rule_credit": """<rule_credit>
[IMMUNITY - REWARD] 
Check the <current_credit_list>. When your normal scheduling sequence naturally reaches these IDs, you MUST SKIP them once (giving them a free pass) as a reward for past extra work.
</rule_credit>""",

    "param_credit": "<current_credit_list>{credit_list}</current_credit_list>",

    # --------------------------------------------------------------

    "rule_inactive": """<rule_inactive>
[HARD CONSTRAINT - UNAVAILABLE] 
IDs listed in <inactive_ids> are currently unavailable (e.g., sick leave, suspended). You MUST completely SKIP them and assign the duty to the next available person in the sequence. Do NOT schedule them under any circumstances.
</rule_inactive>""",

    "param_inactive": "<inactive_ids>{inactive_ids}</inactive_ids>",
    
    # --------------------------------------------------------------

    "rule_multi_day": """<rule_multi_day>
[PATCH PRINCIPLE] 
The user is requesting a schedule spanning multiple days. Count exactly how many days are requested. ONLY generate schedule entries for these specific dates. OVER-GENERATION IS FATAL.
</rule_multi_day>"""