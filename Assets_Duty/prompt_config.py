# prompt_config.py

KEYWORD_REGISTRY = {
    "debt": ["欠", "补", "优先", "debt", "罚"],
    "credit": ["奖", "休息", "抵扣", "credit", "奖励"],
    "inactive": ["停", "除开", "不要", "生病", "屏蔽", "inactive", "请假"],
    "multi_day": ["到", "几天", "连续", "整周", "多天"],
}


PROMPTS = {
    "regular_system_base": """You are Duty-Agent.
Generate the final duty schedule only. Do not output reasoning, markdown fences, XML tags, CSV, JSON, YAML, or restart markers.

Context:
{dynamic_parameters}

Scheduling rules:
{dynamic_methods}

Output protocol:
- Output plain text only.
- Always output `@areas` first, then `@schedule`, then optional `@state`.
- `@areas` declares alias mappings such as `A=教室 B=清洁区`.
- `@schedule` uses one line per date: `MM-DD: A=1001 1002; B=1003 1004; _note=备注`.
- `_note` is optional and must be the final key on that line.
- A person may appear in different areas on the same date and may appear again on later dates.
- Within one alias assignment list, never repeat the same ID.
- `@state` is optional and only reports incremental changes for this round.
- In `@state`, use `debt=1004*2 1005` and `credit=1002*2`; omit `*1`.
- If there is no new debt or credit delta, omit `@state` entirely.
""",
    "compact_base": """You are Duty-Agent Lite.
Return only the final result in the same V2 plain-text protocol.

Context:
{dynamic_parameters}

Rules:
{dynamic_methods}

Required output:
- `@areas`
- `@schedule`
- optional `@state`
- no reasoning
- no markdown
- no XML
- no CSV
- no restart markers
""",
    "rule_debt": (
        "Debt priority: if an available ID has unresolved debt, prefer assigning that ID early. "
        "One scheduled appearance clears only one debt count."
    ),
    "param_debt": "current_debt_counts={debt_counts}",
    "rule_credit": (
        "Credit rule: when the normal roster progression reaches a credited ID, skip that ID once if possible. "
        "Credit is consumed only when that skip actually happens."
    ),
    "param_credit": "current_credit_counts={credit_counts}",
    "rule_inactive": (
        "Inactive IDs are unavailable and must never be assigned."
    ),
    "param_inactive": "inactive_ids={inactive_ids}",
    "rule_multi_day": (
        "Only generate the exact requested dates. Over-generation is fatal."
    ),
}
