# prompt_config.py

KEYWORD_REGISTRY = {
    "debt": ["欠", "补", "优先", "debt", "罚"],
    "credit": ["奖", "休息", "抵扣", "credit", "奖励"],
    "inactive": ["假", "离开", "不要", "生病", "屏蔽", "inactive", "请假"],
    "absent": ["假", "离开", "生病", "缺席", "inactive", "请假"],
    "multi_day": ["到", "几天", "连续", "整周", "多天", "下周", "本周", "这周"],
}

# Single source of truth for the V2 INI wire protocol. Both the full and the
# compact template splice in THIS block: compact_base used to drop the whole
# "Output protocol" section, so small/campus models received a different and
# contradictory grammar (optional [state] vs conditionally-required, no
# credit/consumed_credit, no alias syntax). Keep this in sync with AGENTS.md.
V2_OUTPUT_PROTOCOL = """- Output plain text only. No prose, no reasoning, no markdown fences.
- Always output `[areas]` first, then `[schedule]`, then optional `[state]`.
- `[areas]` declares alias mappings such as `A = 教室` (alias UPPERCASE, e.g. A, B, S).
- `[schedule]` uses one line per date: `MM-DD = A:1001 1002 | B:1003 1004 # 备注`.
  Use `|` between area assignments when a date has several areas; spaces between IDs.
- The trailing `# 备注` comment is optional.
- A person may appear in different areas on the same date and may appear again on later dates.
- Within one alias assignment list, never repeat the same ID.
- `[state]` is OPTIONAL and reports incremental changes for this round. Omit the
  whole section when nothing changed; empty `debt =` / `credit =` lines are also
  valid and mean "no change". Never use `:` in `[state]`.
- In `[state]`, use `debt = 1004*2 1005` and `credit = 1002*2`; omit `*1`.
- In `[state]`, use `pointer = <int>` for a 0-based INDEX into all_roster_ids
  (range 0..N-1), NOT a person ID.
- In `[state]`, use `consumed_credit = 1001 1002` to list IDs whose credit was consumed by pointer advancement this round.
- In `[state]`, use `absent = 1005 1007` to declare IDs that must not be scheduled in this round (leave/sickness found in the instruction).
"""


PROMPTS = {
    "regular_system_base": """You are Duty-Agent.
Generate the final duty schedule only. Do not output reasoning, markdown fences, XML tags, CSV, JSON, YAML, or restart markers.

Context:
{dynamic_parameters}

Scheduling rules:
{dynamic_methods}

Output protocol:
""" + V2_OUTPUT_PROTOCOL,
    "compact_base": """You are Duty-Agent Lite.
Return only the final result in the same V2 plain-text protocol.

Context:
{dynamic_parameters}

Rules:
{dynamic_methods}

Output protocol:
""" + V2_OUTPUT_PROTOCOL,
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
    "rule_absent": (
        "Absent IDs (leave/sickness) are unavailable in this schedule window "
        "and must never be assigned. Declare them with `absent = ...` in [state]."
    ),
    "param_absent": "absent_ids={absent_ids}",
    "rule_day_override": (
        "On dates listed in day_headcount_overrides, the overridden area needs "
        "exactly the overridden headcount (e.g. extra people for a deep-clean "
        "day) instead of its normal per-day count."
    ),
    "param_day_overrides": "day_headcount_overrides={day_overrides}",
    "rule_multi_day": (
        "Only generate the exact requested dates. Over-generation is fatal."
    ),
    "rule_pointer": (
        "Pointer semantics: last_pointer and `[state] pointer` are 0-based INDICES "
        "into all_roster_ids (0..N-1), NOT person IDs. After scheduling the person at "
        "index k, the next pointer is k+1 (wrapping at the end). When user_instruction "
        "names explicit starting IDs, those IDs override last_pointer for the first date; "
        "the backend recomputes the final pointer from the committed schedule."
    ),
    "rule_id_notation": (
        "ID notation: `N#` in user_instruction means roster ID N. Every ID in "
        "[schedule]/[state] must be a numeric ID present in all_roster_ids. The "
        "1001/1002 numbers in the format examples are placeholders, not real IDs."
    ),
    "rule_scheduled_dates": (
        "Date scope: generate exactly the dates listed in scheduled_dates "
        "(weekday_hints gives their weekday). Do not add or omit dates."
    ),
}
