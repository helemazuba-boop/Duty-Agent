# 当前项目“增量模式”提示词整理

本文基于当前仓库源码整理“增量模式”的真实提示词链路。这里的“增量模式”对应方案 `mode_id=incremental_small`，最终解析为 `single_pass_strategy=incremental_thinking`。

## 1. 入口条件

- 方案入口：
  - `incremental_small` 是内置方案之一。
  - 来源：[state_ops.py](C:/Users/ZhuanZ/OneDrive/Desktop/Duty-Agent/Assets_Duty/state_ops.py)
- 运行期配置：
  - 选中 `incremental_small` 时，运行期配置会固定为：
    - `orchestration_mode = single_pass`
    - `single_pass_strategy = incremental_thinking`
  - 来源：[state_ops.py](C:/Users/ZhuanZ/OneDrive/Desktop/Duty-Agent/Assets_Duty/state_ops.py)
- 执行计划：
  - `resolve_execution_profile()` 会保留 `single_pass_strategy=incremental_thinking`
  - `build_execution_plan()` 会把它落到 `runtime_mode=single_pass`
  - 来源：[execution_profiles.py](C:/Users/ZhuanZ/OneDrive/Desktop/Duty-Agent/Assets_Duty/execution_profiles.py)

## 2. Prompt 进入点

- 单通道执行器在真正调用 LLM 前，会走：
  - `run_single_pass_schedule()`
  - `build_single_pass_prompt_messages()`
  - `build_prompt_messages()`
- 来源：
  - [single_pass_executor.py](C:/Users/ZhuanZ/OneDrive/Desktop/Duty-Agent/Assets_Duty/single_pass_executor.py)
  - [prompt_gateway.py](C:/Users/ZhuanZ/OneDrive/Desktop/Duty-Agent/Assets_Duty/prompt_gateway.py)
  - [build_prompt.py](C:/Users/ZhuanZ/OneDrive/Desktop/Duty-Agent/Assets_Duty/build_prompt.py)

一个重要事实：

- 当前增量模式返回给 LLM 的 `messages` 只有一条。
- 这条消息的角色是 `user`，不是 `system`。
- 但消息内容本身包了一层 XML 风格的 `<system_directive>...</system_directive>`。
- 来源：[build_prompt.py](C:/Users/ZhuanZ/OneDrive/Desktop/Duty-Agent/Assets_Duty/build_prompt.py)

## 3. 当前增量模式实际走哪套模板

当前源码里有 3 套基础模板：

- `regular_system_base`
- `compact_base`
- `incremental_base`

但真实分支是：

- 如果是 `campus_small` 或 `multi_agent`，并且 `single_pass_strategy != incremental_thinking`，走 `compact_base`
- 否则走 `regular_system_base`

因此：

- `incremental_thinking` 会强制避开 `compact_base`
- 当前“增量模式”实际走的是 `regular_system_base`
- `incremental_base` 目前存在于 [prompt_config.py](C:/Users/ZhuanZ/OneDrive/Desktop/Duty-Agent/Assets_Duty/prompt_config.py)，但当前分支里没有被调用，属于未接入模板

来源：

- [build_prompt.py](C:/Users/ZhuanZ/OneDrive/Desktop/Duty-Agent/Assets_Duty/build_prompt.py)
- [prompt_config.py](C:/Users/ZhuanZ/OneDrive/Desktop/Duty-Agent/Assets_Duty/prompt_config.py)

## 4. 增量模式的动态参数

无论是否触发额外规则，增量模式都会注入这些参数：

- `all_roster_ids`
- `current_time`
- `user_instruction`
- `area_names`
- `area_slot_counts`
- `single_pass_strategy`
- `previous_run_memory`
  - 仅当 `state.next_run_note` 非空时注入

这些参数来自：

- 花名册
- 当前时间
- 用户指令
- 默认区域配置
- 上次运行记忆

来源：

- [single_pass_executor.py](C:/Users/ZhuanZ/OneDrive/Desktop/Duty-Agent/Assets_Duty/single_pass_executor.py)
- [build_prompt.py](C:/Users/ZhuanZ/OneDrive/Desktop/Duty-Agent/Assets_Duty/build_prompt.py)

## 5. 增量模式的动态规则块

在 `regular_system_base` 下，`processing_steps` 由若干可选规则拼出来。

### 5.1 debt 规则

触发条件：

- `debt_list` 非空，或者
- 指令命中 debt 关键词

注入内容：

- `<current_debt_list>...</current_debt_list>`
- `<rule_debt>...</rule_debt>`

语义：

- 优先补债，先把欠账人员安排掉

### 5.2 credit 规则

触发条件：

- `credit_list` 非空，或者
- 指令命中 credit 关键词

注入内容：

- `<current_credit_list>...</current_credit_list>`
- `<rule_credit>...</rule_credit>`

语义：

- 正常轮转命中到奖励人员时，跳过一次

### 5.3 inactive 规则

触发条件：

- 存在 inactive 人员，或者
- 指令命中 inactive 关键词

注入内容：

- `<inactive_ids>...</inactive_ids>`
- `<rule_inactive>...</rule_inactive>`

语义：

- 不可用人员必须完全跳过

### 5.4 multi_day 规则

触发条件：

- 指令命中多天排班关键词

注入内容：

- `<rule_multi_day>...</rule_multi_day>`

语义：

- 只生成明确请求的那些日期，禁止过量生成

### 5.5 user_defined_rule

触发条件：

- `duty_rule` 非空

注入内容：

- `<user_defined_rule>...</user_defined_rule>`

### 5.6 output_guard

始终注入：

- 只允许输出该日期被安排的 ID
- 必须严格满足槽位数
- 多 ID 单元格必须 CSV 正确引用

### 5.7 execution_hint

这是增量模式专属附加块，只有 `single_pass_strategy == incremental_thinking` 才会注入：

```xml
<execution_hint>
Think through the constraints carefully before finalizing the schedule, but only return the final result.
</execution_hint>
```

来源：

- [build_prompt.py](C:/Users/ZhuanZ/OneDrive/Desktop/Duty-Agent/Assets_Duty/build_prompt.py)
- [prompt_config.py](C:/Users/ZhuanZ/OneDrive/Desktop/Duty-Agent/Assets_Duty/prompt_config.py)

## 6. 当前增量模式的基础外壳

增量模式当前实际使用的基础外壳是：

```xml
<system_directive>
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
```

来源：[prompt_config.py](C:/Users/ZhuanZ/OneDrive/Desktop/Duty-Agent/Assets_Duty/prompt_config.py)

## 7. 当前增量模式的“接近真实运行态”模板

把上面的参数和规则拼起来，当前增量模式最接近真实运行态的模板可以整理成这样：

```xml
<system_directive>
<role>Duty-Agent</role>
<task>Schedule balancing Hard(Sick/Inactive) & Soft Constraints + Fairness(Debt)</task>
<process_guideline>Process the information below in turns.</process_guideline>

<context_parameters>
<all_roster_ids>{all_ids}</all_roster_ids>
<current_time>{current_time}</current_time>
<user_instruction>{instruction}</user_instruction>
<area_names>{area_names}</area_names>
<area_slot_counts>{area_slot_counts}</area_slot_counts>
<single_pass_strategy>incremental_thinking</single_pass_strategy>
<previous_run_memory>{previous_context_if_any}</previous_run_memory>
<current_debt_list>{debt_ids_if_any}</current_debt_list>
<current_credit_list>{credit_ids_if_any}</current_credit_list>
<inactive_ids>{inactive_ids_if_any}</inactive_ids>
</context_parameters>

<processing_steps>
{rule_debt_if_triggered}
{rule_credit_if_triggered}
{rule_inactive_if_triggered}
{rule_multi_day_if_triggered}
{user_defined_rule_if_any}
<output_guard>
Assigned_IDs must contain ONLY the scheduled IDs for that date, not the whole roster.
If area_slot_counts says default_area=2, then each date must contain exactly 2 unique IDs.
When Assigned_IDs contains multiple IDs in one CSV cell, quote that cell and separate IDs with commas, for example "1,2".
</output_guard>
<execution_hint>
Think through the constraints carefully before finalizing the schedule, but only return the final result.
</execution_hint>
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
```

## 8. 当前实现的几个关键结论

- “增量模式”本质上是：
  - `single_pass`
  - `single_pass_strategy=incremental_thinking`
  - `regular_system_base`
  - `execution_hint` 专属增强

- 当前并不存在一条真正独立的“incremental 专用模板链路”。
  - `incremental_base` 虽然定义了，但没有被实际使用。

- 所以如果后续你要继续重构“增量模式提示词”，最直接的切入点有两个：
  - 在 [build_prompt.py](C:/Users/ZhuanZ/OneDrive/Desktop/Duty-Agent/Assets_Duty/build_prompt.py) 里给 `incremental_thinking` 单独接 `incremental_base`
  - 或者保留 `regular_system_base`，但把 `execution_hint` 扩成一整套增量模式专属 rules

