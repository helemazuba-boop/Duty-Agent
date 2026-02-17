# Duty-Agent AI 排班核心流程深度技术报告

本文档在初步研究的基础上，进一步深入剖析 Duty-Agent 的提示词（Prompt）组成结构、发送逻辑、AI 推理过程及结果解析（Parsing）的底层实现，并详细追溯上下文中关键变量的数据来源。

## 1. 提示词（Prompt）的精密组成

提示词的构建在 `Assets_Duty\core.py` 的 `build_prompt_messages` 函数中完成。它是通过拼接多个功能性片段形成的。

### 1.1 System Prompt (系统指令层)
这是 AI 的“运行底座”，决定了它的行为边界和输出确定性。其组成如下：

1.  **角色定义与输出基调**：
    *   内容：`"You are a scheduling engine."`
    *   作用：将模型从“聊天机器人”转换为“逻辑执行引擎”。
2.  **强制性协议**：
    *   内容：`"Only process numeric IDs and output strict JSON. Do not output extra explanations."`
    *   作用：通过负向约束防止模型输出说明文字。
3.  **JSON Schema 定义（动态构建）**：
    *   逻辑：系统根据当前定义的区域名称（`area_names`）动态生成。
4.  **业务逻辑注入 (Duty Rule)**：
    *   逻辑：包裹在 `--- Rules ---` 块中的用户自定义字符串。

### 1.2 User Prompt (任务上下文层)
为 AI 提供当前运行的实时快照，使其知道“从哪里开始，排给谁”。

---

## 2. 关键变量的“源头”追踪

上下文变量并非凭空产生，而是由 C# 宿主准备并由 Python 脚本在运行瞬间计算出来的。

### 2.1 基础配置类变量 (来自 `config.json`)
这些变量由用户在 UI 界面输入，保存在 `Assets_Duty\data\config.json` 中，C# 启动 Python 进程时通过 `ipc_input.json` 传递给 `core.py`。

*   **`instruction` (指令)**：
    *   **来源**：`DutyMainSettingsPage.axaml` 中的 `InstructionBox` 文本框。
    *   **默认值**：如果文本框为空，C# 会从 `DutyBackendService.cs` 中提取常量 `AutoRunInstruction`。
*   **`duty_rule` (长期规则)**：
    *   **来源**：`DutyMainSettingsPage.axaml` 中的 `DutyRuleBox` 文本框。
    *   **持久化**：存储在 `config.json` 的 `duty_rule` 字段中。
*   **`area_names` (区域名称)**：
    *   **来源**：由 C# 端的 `GetAreaNames()` 函数提供。它会扫描 `state.json` 中已有的排班记录，提取出所有出现过的区域键名（如“教室”、“清洁区”）。如果没有任何记录，则使用硬编码的默认值。

### 2.2 状态类变量 (来自 `state.json` 与 `roster.csv`)
这些变量描述了“排班的进度”和“参与的人员”，由 `core.py` 在运行瞬间实时计算。

*   **`id_range` (ID 范围)**：
    *   **来源**：`Assets_Duty\data\roster.csv`。
    *   **计算逻辑**：Python 脚本读取 CSV 文件，查找 `id` 列的最小值和最大值（例如 `1-45`）。这告诉 AI 学生编号的有效区间。
*   **`disabled_ids` (禁用 ID)**：
    *   **来源**：`Assets_Duty\data\roster.csv`。
    *   **计算逻辑**：过滤出 CSV 中 `active` 列为 `0` 的行。
    *   **意义**：明确告知 AI 哪些学生（如请假、休学）不能参与本次排班。
*   **`last_id` (上一个值日生 ID)**：
    *   **来源**：`Assets_Duty\data\state.json`。
    *   **核心逻辑**：
        1.  Python 读取 `state.json` 中的 `schedule_pool`（排班历史池）。
        2.  定位到日期最晚的那一天。
        3.  扫描该天所有区域分配的学生 ID。
        4.  取其中最大的 ID（或最后一个分配的 ID）作为 `last_id`。
    *   **作用**：AI 看到这个 ID 后，会从 `last_id + 1` 开始往后轮排，实现“公平轮换”。

### 2.3 环境类变量 (系统提供)
*   **`current_time` (当前时间)**：
    *   **来源**：Python 脚本调用 `datetime.now()`。
    *   **作用**：作为 AI 理解“明天”、“本周五”等相对时间概念的基准锚点。

---

## 3. 数据流转图

1.  **用户操作**：在 UI 输入 `instruction` -> C# 接收。
2.  **启动准备**：C# 读取 `config.json` -> 写入 `ipc_input.json` -> 启动 Python。
3.  **计算上下文**：Python 读取 `ipc_input.json` + `roster.csv` + `state.json` -> **计算出 `id_range` 和 `last_id`**。
4.  **构建 Prompt**：将上述所有变量填充进模板 -> 发送给 AI。
5.  **回写结果**：AI 返回 JSON -> Python 解析并校验 -> 写入 `state.json`。

---

## 4. 总结
Duty-Agent 的上下文变量形成了一个闭环：**Roster (谁在)** + **State (排到谁了)** + **Config (怎么排)**。通过在 Python 侧动态计算 `last_id` 和 `id_range`，系统保证了即使在多次运行或中断后，AI 依然能准确接上手头的任务，维持排班的连续性和公平性。
