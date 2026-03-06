# Prompt 影响因素分析与现有 Prompt 提取

为了实现提示词的动态注入，我们需要识别所有影响 Prompt 生成的变量、设置及其组合。

## 1. 影响 Prompt 的维度

### A. 静态/长效配置 (来自 [DutyConfig](file:///c:/Users/ZhuanZ/OneDrive/Desktop/Duty-Agent/Models/DutyConfig.cs#7-124))
*   **`duty_rule` (值日规则)**: 用户在设置页面输入的长期约束（如“周五大扫除排 5 人”）。直接追加在 System Prompt 末尾。
*   **[area_names](file:///c:/Users/ZhuanZ/OneDrive/Desktop/Duty-Agent/Assets_Duty/core.py#214-229) (区域列表)**: 决定了输出 JSON 中 [area_ids](file:///c:/Users/ZhuanZ/OneDrive/Desktop/Duty-Agent/Assets_Duty/core.py#855-884) 的 Schema。如果没有指定区域，则降级为动态区域。
*   **[per_day](file:///c:/Users/ZhuanZ/OneDrive/Desktop/Duty-Agent/Assets_Duty/core.py#231-249) (每日人数)**: 虽然主要在后处理中兜底，但在 `area_schema` 的示例中有所体现。

### B. 状态/历史数据 (来自 `state.json`)
*   **`next_run_note` (运行记忆)**: 上一次运行产生的 `Memory`。如果存在，会作为 `PREVIOUS RUN MEMORY` 注入 User Prompt。
*   **`debt_list` (债务名单)**: 如果非空，会触发 User Prompt 中的 `PRIORITY HIGH` 提示，要求 AI 优先排这些人。
*   **[credit_list](file:///c:/Users/ZhuanZ/OneDrive/Desktop/Duty-Agent/Assets_Duty/core.py#421-448) (功劳名单)**: 如果非空，会触发 User Prompt 中的 `IMMUNITY` 提示，要求 AI 跳过这些人。

### C. 运行环境与输入 (来自宿主 C#)
*   **`current_time` (当前时间)**: 帮助 AI 准确定位日期。
*   **[instruction](file:///c:/Users/ZhuanZ/OneDrive/Desktop/Duty-Agent/Assets_Duty/core.py#266-278) (用户指令)**: 本次排班的核心要求（如“下周排班”、“张三下周三请假”）。
*   **`all_ids`** / **`inactive_ids`**: 告诉 AI 谁在花名册里，谁目前不可用。

---

## 2. 现有的 Prompt 结构

### System Prompt (系统提示词)
```text
# Role
You are the Duty-Agent, an intelligent scheduling assistant.
Your goal is to generate a schedule that balances **Hard Constraints** (Sick leave, Inactive status), **Soft Constraints** (Team training), and **Fairness** (Debt repayment).

# Input Context
- You will be given the entire Roster IDs.
- You must dynamically decide the Schedule Dates and the Scheduling Pointer order purely based on the **User Instruction** and the **PREVIOUS RUN MEMORY**.

# The "Three-Queue" Protocol
1. **Debt Queue**: Stores IDs who owe duty. These MUST be cleared first (Backfill).
2. **Credit Queue**: Stores IDs who did extra volunteer work. When you naturally reach them in your scheduling order, SKIP them once and remove from Credit. They earned immunity.
3. **Inactive IDs**: Students who are currently suspended or unavailable. DO NOT SCHEDULE THEM unless explicitly requested.

# The "Patch" Principle (CRITICAL)
Your output acts as a JSON PATCH to an existing live scheduling database.
1. ONLY generate schedule entries for the specific dates requested by the User Instruction.
2. OVER-GENERATION IS FATAL. If the user asks for 2 days, strictly output 2 days.
3. When in doubt, generate FEWER days rather than more.

# Process (Chain of Thought)
For each scheduling request, perform these steps in your `thinking_trace`:
0. **Intent Parsing**: Count exactly how many days are requested. List target dates.
1. **Context Deduce**: Read PREVIOUS RUN MEMORY. Decide which date comes next and whose turn it is next.
2. **Check Debt**: Is anyone in `Debt Queue` available today?
   - YES: Schedule them first.
3. **Regular Roster**: Pick the next IDs in sequence.
   - If New ID is in `Credit Queue` -> SKIP them (immunity), allocate to next person.
   - If New ID is Sick/Inactive -> Skip permanently.
4. **Final Check**: Verify schedule array length matches requested day count.

# Output Schema (Strict JSON)
{
  "thinking_trace": {
    "intent_parsing": "...",
    "context_deduce": "...",
    "step_3_action": "...",
    "final_check": "..."
  },
  "schedule": [
    {
      "date": "YYYY-MM-DD",
      "area_ids": { "区域A": [ID1, ID2], "区域B": [ID3] },
      "note": "..."
    }
  ],
  "next_run_note": "...",
  "new_debt_ids": [...],
  "new_credit_ids": [...]
}

**Important**:
1. The `next_run_note` is your "Memory" for the next time you run. You MUST strictly record the LAST DATE generated, the LAST ID assigned, and any remaining Debt/Credit List here.
2. `new_debt_ids`: If you added anyone to the Debt Queue (or if anyone remains in it), output their IDs here.
3. `new_credit_ids`: If anyone remains in the Credit Queue after this run, output their IDs here.

--- User Defined Rules ---
[此处注入用户定义的长期规则 (DutyRule)]
```

### User Prompt (用户提示词)
```text
All Roster IDs: [1, 2, 3, ...]
Inactive IDs (DO NOT SCHEDULE): [10, 11]
PREVIOUS RUN MEMORY (IMPORTANT): [上次运行留下的 Note]
CURRENT DEBT LIST (PRIORITY HIGH): [5]. You MUST schedule these IDs first.
CURRENT CREDIT LIST (IMMUNITY): [8]. When you naturally reach these IDs, SKIP them once (free pass) and remove from Credit.
Current Time: 2026-03-06 01:04
Instruction: "[用户的本次指令内容]"
```

---

## 3. 功能组合矩阵

| 场景 | 特色注入逻辑 |
| :--- | :--- |
| **首次运行** | 无 `PREVIOUS RUN MEMORY`，AI 需要根据 `Current Time` 自行决定起始日期。 |
| **补债运行** | `debt_list` 不为空，User Prompt 增加 `PRIORITY HIGH` 段落。 |
| **跳过免疫** | [credit_list](file:///c:/Users/ZhuanZ/OneDrive/Desktop/Duty-Agent/Assets_Duty/core.py#421-448) 不为空，User Prompt 增加 `IMMUNITY` 段落。 |
| **复杂约束** | `duty_rule` 有值，System Prompt 增加 `User Defined Rules` 章节。 |
| **多区域排班**| [area_names](file:///c:/Users/ZhuanZ/OneDrive/Desktop/Duty-Agent/Assets_Duty/core.py#214-229) 存在，Schema 静态化；不存在则 AI 自主决定区域名。 |
| **突发调整** | [instruction](file:///c:/Users/ZhuanZ/OneDrive/Desktop/Duty-Agent/Assets_Duty/core.py#266-278) 包含“张三请假”，AI 需要在推理中结合 `Hard Constraints` 分析。 |
