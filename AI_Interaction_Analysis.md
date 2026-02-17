# Duty-Agent AI 交互流与提示词工程深度分析报告

本报告详细阐述了 **Duty-Agent** 项目中 AI 排班核心模块的实现细节。我们将深入剖析从 C# 宿主程序发起请求，到 Python 代理程序构建提示词、调用大模型 API，以及最后解析结果并回填数据的全过程。

---

## 1. 架构总览与交互生命周期

Duty-Agent 采用的是经典的 **宿主-代理 (Host-Agent)** 架构。为了保证核心排班逻辑的灵活性和跨平台能力（以及利用 Python 强大的数据处理生态），项目将业务逻辑解耦为两部分：

1.  **C# 宿主 (Host)**: 负责用户界面、配置管理、进程调度以及结果展示。
2.  **Python 代理 (Agent)**: 负责提示词构建、与 LLM (如 OpenAI/DeepSeek) 通信、以及复杂的排班算法兜底。

### 交互时序图

交互过程是一个严格的线性流水线，确保了数据的一致性和可追溯性：

1.  **用户触发**: 用户在 UI 点击“自动排班”或通过定时任务触发。
2.  **数据准备 (C#)**: 宿主程序打包所有必要的上下文（花名册状态、历史排班、用户指令）。
3.  **IPC 通信**: 数据被序列化为 `ipc_input.json`，API Key 通过标准输入 (Stdin) 安全传输。
4.  **代理启动 (Python)**: `core.py` 启动，读取配置，加载数据。
5.  **提示词工程**: Python 脚本根据当前状态构建 System Prompt 和 User Prompt。
6.  **大模型推理**: 调用 LLM API，获取 JSON 格式的排班建议。
7.  **结果解析与兜底**: Python 脚本解析 JSON，校验 ID 有效性，并执行“轮询补位”算法确保排班完整。
8.  **状态回写**: 结果写入 `ipc_result.json`，并原子更新 `state.json`。
9.  **UI 刷新 (C#)**: 宿主检测到进程结束，读取结果并刷新界面。

---

## 2. 数据准备与 IPC 通信 (C# 侧)

**核心文件**: `Services/DutyBackendService.cs`

在调用 Python 核心之前，C# 必须准备好所有“原材料”。这是通过 `RunCoreAgentWithMessage` 方法实现的。

### 2.1 上下文聚合

C# 不会将原始的业务对象直接扔给 Python，而是构建一个专门的 **传输对象 (DTO)**，仅包含推理所需的数据。这降低了耦合度。

```csharp
var inputData = new
{
    instruction = effectiveInstruction,      // 用户指令，如 "下周三个人值日"
    apply_mode = applyMode,                  // 模式：追加(append) / 覆盖(replace)
    start_from_today = Config.StartFromToday,// 是否从今天开始
    days_to_generate = Config.AutoRunCoverageDays, // 生成天数
    per_day = Config.PerDay,                 // 每天人数
    skip_weekends = Config.SkipWeekends,     // 跳过周末
    duty_rule = Config.DutyRule,             // 用户自定义规则 (关键)
    base_url = Config.BaseUrl,               // LLM API 地址
    model = overrideModel ?? Config.Model    // 模型名称
};
```

这个对象被序列化并写入 `Assets_Duty/data/ipc_input.json`。

### 2.2 安全性设计：API Key 的传输

**关键点**: **API Key 绝不写入磁盘**。

为了防止 API Key 泄露（例如存入日志或临时文件），Duty-Agent 采用 **标准输入 (Stdin)** 管道传输密钥。

1.  C# 启动 Python 进程，重定向 `StandardInput`。
2.  C# 在进程启动后立即写入 API Key 及其后的换行符。
3.  C# 关闭输入流。
4.  Python 端 (`core.py`) 启动时优先尝试从 `sys.stdin` 读取密钥，读取完毕后内存驻留，不落地。

---

## 3. 提示词工程 (Prompt Engineering)

**核心文件**: `Assets_Duty/core.py` -> `build_prompt_messages`

这是整个系统的灵魂。为了让通用大模型（LLM）变成一个精准的“排班引擎”，我们采用了 **结构化提示词 (Structured Prompting)** 技术。

### 3.1 System Prompt：确立“人设”与“规则”

System Prompt 的作用是“催眠”模型，让它忘记自己是一个聊天机器人，而认为自己是一个 JSON 生成器。

**核心指令拆解**:

1.  **人设定义**:
    > "You are a scheduling engine."
    > (你是一个排班引擎。)
    > 确立了专业、客观、非对话式的基调。

2.  **格式强约束**:
    > "Only process numeric IDs and output strict JSON. Do not output extra explanations."
    > (只处理数字 ID，输出严格的 JSON。不要输出额外的解释。)
    > 这是为了防止模型输出 "Here is your schedule:" 这样的废话，导致 JSON 解析失败。

3.  **Schema 注入 (One-Shot Learning)**:
    为了确保模型理解输出格式，我们在 System Prompt 中直接给出了一个 JSON 模板。这个模板是动态生成的，会根据用户配置的区域（如“教室”、“清洁区”）自动调整。

    ```json
    Output schema:
    {
      "schedule": [
        { "day": "Mon", "area_ids": { "教室": [101, 102] } },
        { "day": "Tue", "area_ids": { "教室": [105, 106] } }
      ]
    }
    ```
    通过提供示例，模型不需要猜测字段名，极大提高了稳定性。

4.  **规则注入**:
    用户的自定义规则（如“男生必须做清洁区”）会被附加在 System Prompt 的末尾：
    > "--- Rules ---"
    > "{User Defined Rules}"

### 3.2 User Prompt：注入“动态上下文”

System Prompt 是静态的规则，而 User Prompt 包含了每次运行时的动态约束。我们采用了 **键值对 (Key-Value)** 的形式来清晰地传达参数。

**提示词模板**:

```text
ID Range: {min_id}-{max_id}
Disabled IDs: {list_of_disabled_ids}
Last ID: {last_anchor_id}
Current Time: {YYYY-MM-DD HH:MM}
Instruction: "{sanitized_instruction}"
```

**参数详解**:

1.  **ID Range (ID 范围)**:
    告诉模型 ID 的合法区间（例如 1-50）。这有助于模型建立数字空间的概念。

2.  **Disabled IDs (禁用 ID)**:
    这是系统自动计算得出的。如果用户在花名册中将某个学生标记为“不活跃”（如休学、转学），这个学生的 ID 会出现在这里。模型被隐式要求避开这些 ID。

3.  **Last ID (锚点 ID)**:
    **这是轮询算法的核心**。
    排班通常需要遵循“轮流”原则。为了让 LLM 知道“上次轮到谁了”，我们必须传入 `Last ID`。
    例如，如果上次排班结束于 ID 15，那么模型应该倾向于从 ID 16 开始排。

4.  **Current Time (当前时间)**:
    虽然模型主要处理相对逻辑，但提供时间有助于某些特定指令（如“排下周的班”）。

5.  **Instruction (用户指令)**:
    用户的自然语言需求。

### 3.3 隐私与稳定性：指令匿名化 (Anonymization)

这是一个非常重要的预处理步骤。LLM 处理数字逻辑的能力远强于处理中文姓名，且直接发送姓名可能涉及隐私问题（取决于部署环境）。

**逻辑**: `anonymize_instruction` 函数

1.  Python 脚本加载 `roster.csv`，构建 `姓名 -> ID` 的映射表。
2.  扫描用户的指令字符串。
3.  将所有出现的姓名替换为对应的 ID。

**示例**:
*   **用户输入**: "让张三和李四负责周五的卫生。"
*   **发送给 LLM**: "让 12 和 34 负责周五的卫生。"

这样，模型只需要处理纯数学逻辑，不需要关心名字的复杂性（如生僻字、同音字）。

---

## 4. 大模型调用与网络交互

**核心文件**: `Assets_Duty/core.py` -> `call_llm`

网络是不稳定的，LLM 的也会偶尔“抽风”。代码中包含了多层防御机制。

### 4.1 请求构造

使用 Python 原生 `urllib` 库（为了减少依赖体积，不依赖 `requests` 库），构造标准的 OpenAI 格式请求。

*   **Model**: 用户配置的模型名。
*   **Messages**: 上文构建的 System + User 消息列表。
*   **Temperature**: **0.1**。
    *   这是一个极低的温度值。排班任务需要的是**逻辑与确定性**，不需要创造性。低温度能显著减少模型的幻觉，确保它严格遵守 JSON 格式和轮询规则。

### 4.2 重试机制 (Retry Logic)

代码实现了一个指数退避 (Exponential Backoff) 的重试循环：

1.  **捕获异常**: HTTP 429 (Rate Limit)、5xx (Server Error)、Timeout。
2.  **策略**:
    *   最大重试次数: 2次（共3次尝试）。
    *   等待时间: 每次重试等待 `2秒 * (attempt + 1)`。
3.  **超时控制**: 硬性超时设置为 120秒，防止进程无限挂起。

---

## 5. 结果解析与“兜底”逻辑 (The Safety Net)

这是 Duty-Agent 最核心的价值所在。它不完全信任 AI，而是将其输出视为“建议”，并用传统算法进行清洗和补全。

### 5.1 JSON 清洗

LLM 经常会在 JSON 外面包裹 Markdown 标记（如 ```json ... ```）。`clean_json_response` 函数使用正则表达式：
`re.search(r"\{.*\}", text, re.DOTALL)`
暴力提取第一个左大括号和最后一个右大括号之间的内容，确保 JSON 解析器能拿到纯净的字符串。

### 5.2 ID 有效性校验

解析出的 JSON 包含若干 ID 列表。代码会遍历这些 ID：
1.  **存在性检查**: ID 是否在花名册中？
2.  **活跃状态检查**: 该 ID 是否被禁用？

**任何无效 ID 都会被丢弃**。这防止了 AI“捏造”不存在的学生。

### 5.3 智能轮询补位算法 (Round-Robin Backfill)

**核心函数**: `fill_rotation_ids`

如果 AI 偷懒了（只排了1个人，但要求2个人），或者 AI 排的某些人无效（已毕业），或者 AI 甚至返回了空列表，**系统必须保证排班的连续性**。

这个算法保证了：无论 AI 输出多么离谱，最终生成的排班表一定是**完整**且**连续**的。

**算法流程**:

1.  **输入**:
    *   `Initial IDs`: AI 建议的 ID 列表（已过滤有效性）。
    *   `Demand`: 目标人数 (Per Day)。
    *   `Pointer`: 全局轮询指针 (Last ID)。

2.  **第一阶段：采纳建议**:
    *   优先将 `Initial IDs` 加入最终名单。
    *   如果人数已满，停止。

3.  **第二阶段：算法补位**:
    *   如果人数未满（例如 AI 只给了 1 个，我们需要 2 个）。
    *   从 `Pointer + 1` 开始，沿着花名册（按 ID 排序）向后寻找。
    *   跳过 `Disabled IDs`。
    *   跳过 **本组已选** 的 ID（防止同一个人一天被排两次）。
    *   将找到的 ID 加入名单，直到人数满足 `Demand`。

4.  **第三阶段：指针更新**:
    *   记录本组最后一个被选中的 ID。
    *   将其作为下一天（或下一个区域）的起始 `Pointer`。

**多区域支持**:
系统支持多个区域（如“教室”、“清洁区”）。算法会依次处理每个区域，**指针是连续传递的**。
即：周一教室 -> 周一清洁区 -> 周二教室 -> ...
这确保了工作量的绝对公平分配，不会出现某个人虽然轮到了但被 AI 遗漏从而长期逃避值日的情况。

---

## 6. 状态管理与持久化

### 6.1 状态合并策略

生成的排班表（`restored_schedule`）需要合并回 `state.json`。这不仅仅是简单的追加。

*   **Mode: Append (追加)**:
    在现有排班表的末尾继续追加。适用于常规的“续排”。
*   **Mode: Replace Future (替换未来)**:
    保留“此时此刻”之前的历史记录，删除所有未来的排班，由于新生成的排班替代。这用于“修正接下来的排班”。
*   **Mode: Replace All (全量替换)**:
    清空所有历史，从头开始。

### 6.2 原子写入 (Atomic Write)

为了防止在写入文件时断电或程序崩溃导致 `state.json` 损坏（变成 0 字节），代码实现了原子写入：

1.  写入临时文件 `state.json.tmp`。
2.  调用 `fsync` 确保数据刷入磁盘物理介质。
3.  使用 `os.replace` 将临时文件重命名为正式文件。
    *   在 POSIX 和现代 Windows 系统上，重命名操作通常是原子的。

---

## 7. 常见问题与故障排查 (Troubleshooting)

在实际部署中，用户可能会遇到的一些常见问题及其技术层面的原因与解决方案。

### 7.1 "AI 响应为空" 或 "JSON 解析失败"

**现象**: UI 显示生成失败，错误日志提示 "JSONDecodeError"。
**原因**:
1.  LLM 输出的内容不是合法 JSON（例如包含了过多的解释性文字）。
2.  网络连接在传输中途断开，导致 JSON 被截断。
3.  System Prompt 的 Schema 示例过于复杂，导致小参数模型（如 7B 以下）无法理解。
**解决方案**:
*   检查 Temperature 是否设置为 0.1。
*   检查 `clean_json_response` 的正则是否能覆盖所有 Markdown 变体。
*   尝试更换更强大的模型（如 GPT-4o 或 DeepSeek-V3）。

### 7.2 "排班结果重复" (同一人连续两天值日)

**现象**: 张三周一值日，周二又值日。
**原因**:
1.  花名册人数过少，导致轮询周期短于排班周期。
2.  `Disabled IDs` （禁用列表）包含人数过多，导致可选池子变小。
3.  AI 的随机性导致其未能遵循 "Last ID" 的指示。
**解决方案**:
*   增加花名册人数。
*   在 Prompt 中强调 "Avoid repeating IDs from previous day"。
*   注意：当前的 `fill_rotation_ids` 算法仅保证**单日内不重复**，跨日重复需要依赖 LLM 的智能或后续算法增强。

### 7.3 "无法启动 Python 进程"

**现象**: C# 端直接报错 "FileName not found"。
**原因**:
1.  用户未安装 Python，且内置的嵌入式 Python 环境损坏。
2.  系统环境变量 PATH 未包含 Python。
**解决方案**:
*   `DutyBackendService.cs` 中有 `ValidatePythonPath` 逻辑，会优先检查嵌入式路径。确保嵌入式 Python 包完整。

---

## 8. 未来优化建议 (Future Optimization)

为了进一步提升系统的稳定性和用户体验，以下是一些技术演进方向：

### 8.1 流式输出 (Streaming Output)

当前架构是**全量等待**：用户必须等待 LLM 生成完所有 token（可能需要 10-20 秒）才能看到结果。
*   **改进**: 修改 C# 与 Python 的通信协议，支持流式传输。Python 每收到一个 JSON 对象（如一天的排班），就立即通过 stdout 发送给 C#。
*   **效果**: 用户可以实时看到每一天的排班生成过程，体验更流畅。

### 8.2 本地 LLM 支持 (Local Inference)

依赖云端 API 存在隐私和网络风险。
*   **改进**: 集成 `Ollama` 或 `llama.cpp` 的 Python 用于本地推理。
*   **挑战**: 本地小模型（如 Qwen-7B-Int4）的指令遵循能力较弱，可能需要针对性的 Prompt 调优（Few-Shot Learning）。

### 8.3 智能冲突检测

当前的系统主要依赖“轮询补位”。
*   **改进**: 引入“约束求解器 (Constraint Solver)”。
*   **逻辑**: 在 AI 生成后，使用 Python 的 `constraint` 库进行二次校验，确保满足更复杂的规则（如“张三和李四不能在同一天”）。

---

## 附录：核心代码片段 (Appendix)

为了便于开发者理解，以下摘录了 `core.py` 中最关键的两个函数实现。

### A. 提示词构建器

```python
def build_prompt_messages(
    id_range: Tuple[int, int],
    disabled_ids: List[int],
    last_id: int,
    current_time: str,
    instruction: str,
    duty_rule: str,
    area_names: List[str],
) -> List[dict]:
    # 动态生成区域 Schema
    area_schema = ", ".join([f'"{name}": [101, 102]' for name in area_names])
    
    # System Prompt 模板
    system_parts = [
        "You are a scheduling engine.",
        "Only process numeric IDs and output strict JSON.",
        "Do not output extra explanations.",
        "Output schema:",
        "{",
        '  "schedule": [',
        f'    {{"day": "Mon", "area_ids": {{{area_schema}}}}},',
        # ... 省略部分 ...
        "  ]",
        "}",
        # ...
    ]
    
    if duty_rule:
        system_parts.append("--- Rules ---")
        system_parts.append(duty_rule)
        
    system_prompt = "\n".join(system_parts)

    # User Prompt 模板
    user_parts = [
        f"ID Range: {id_range[0]}-{id_range[1]}",
        f"Disabled IDs: {disabled_ids}",
        f"Last ID: {last_id}",
        f"Current Time: {current_time}",
        f'Instruction: "{instruction}"'
    ]
    user_prompt = "\n".join(user_parts)

    return [
        {"role": "system", "content": system_prompt},
        {"role": "user", "content": user_prompt},
    ]
```

### B. 轮询补位算法

```python
def fill_rotation_ids(
    initial_ids: List[int],
    active_ids: List[int],
    last_index: int,
    per_day: int,
    avoid_ids: Optional[Set[int]] = None,
) -> Tuple[List[int], int]:
    result = []
    
    # 1. 优先采纳 AI 的建议 (已清洗过)
    for pid in initial_ids:
        if pid in active_ids and (avoid_ids is None or pid not in avoid_ids):
            result.append(pid)
            if len(result) >= per_day:
                break

    # 2. 如果人数不足，开始算法补位
    curr_idx = (last_index + 1) % len(active_ids)
    start_idx = curr_idx
    
    while len(result) < per_day:
        pid = active_ids[curr_idx]
        
        # 确保不与今日已选人员重复
        if pid not in result and (avoid_ids is None or pid not in avoid_ids):
            result.append(pid)
            
        curr_idx = (curr_idx + 1) % len(active_ids)
        if curr_idx == start_idx:
            break # 防止死循环（全员都不可用时）

    # 返回最终结果和新的指针位置
    return result, (curr_idx - 1 + len(active_ids)) % len(active_ids)
```

---

## 9. 总结

Duty-Agent 的 AI 交互模块不仅仅是一个简单的 API 调用器。它是一个**混合智能系统**：

1.  **LLM 负责“理解”与“灵活性”**: 它处理自然语言指令，理解复杂的“男生去搬水”这类非结构化规则。
2.  **Python 算法负责“兜底”与“公平性”**: 它利用严格的轮询算法，填补 LLM 的逻辑漏洞，确保名单的每一天都人数充足，且每个人都按顺序轮流值日。

这种设计既利用了 AI 的强大能力，又规避了 AI 不可控的风险，是企业级/生产级 AI 应用的典型范式。
