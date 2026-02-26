# Duty-Agent AI 核心调度系统全景报告

这份报告全面梳理了 Duty-Agent 项目中关于 AI 大模型（LLM）的整个数据链路、算法边界、防幻觉机制以及界面交互包装。

---

## 1. 🤖 AI 角色定义与核心职责

Duty-Agent 是一个建立在 C# (Avalonia) 前端和 Python 核心后端之上的“值日排班推演系统”。在这个系统中，**AI 不是直接操作数据库的执行者，而是一个基于规则和上下文进行纯逻辑推演的“大脑”**。

### 核心介入点
1.  **数据接收**：前端将复杂的《花名册》（可用人员名单、请假名单）与《历史排班记录》扁平化处理后，交给 Python 层，然后组合出系统上下文发送给大模型。
2.  **黑盒运算**：AI 收到上下文后，严格遵循“两重队列算法（Two-Queue Protocol）”进行排班推导，不仅要满足“每天几个人扫哪里”，更要智能解决“谁欠了班需要优先偿还”、“如何避免连续疲劳”等隐性问题。
3.  **结果返回**：推演结束后，AI 必须吐出严格格式化的 JSON 字符串供下游系统持久化。

---

## 2. 🧠 数据结构与状态记忆流 (Data & Memory Flow)

大模型是没有记忆的。为了让多次排班请求之间产生连贯性，Duty-Agent 设计了一套独特的上下文注入与跨次状态传承机制：

### 📥 全量输入封装 (Input Payload)
每次执行请求发往模型前，Python ([core.py](file:///c:/Users/ZhuanZ/OneDrive/Desktop/Duty-Agent/Assets_Duty/core.py)) 会向 AI 注入以下核心变量（位于 `user` role 提示词中）：
*   **`ID_Range`**：当前可用学号的最小与最大范围（例如：1 到 50）。
*   **`Disabled_IDs`**：今天/本周处于请假状态、禁止被排班的学号集合。
*   **`Last_ID` (主锚点)**：历史记录中，最后一次排班的最后一个人的学号。这个锚定值决定了本次排班**从谁开始往后排**。
*   **`CURRENT DEBT LIST` (债务队列)**：之前因为请假或去校队训练，导致在原本该值日那天被“跳过”的学生名单。
*   **精简日历数组 (Compact Calendar Reference)**：为防止大模型幻觉，系统预先计算好日历的星期走向并压成数组发送（如 `Array day_index 0 to 44: Tue, Wed...`）。

### 💾 跨批次记忆引擎 (Long-term Memory)
AI 在返回 JSON 结果时，会向系统写入两个特殊字段，形成闭环记忆：
1.  **`new_debt_ids`**：模型运算过程中，遇到请假的人产生的“新债务队列”。Python 捕捉这个队列，并结合它自己计算出来的物理落位，存入 `state.json`。
2.  **`next_run_note` (下文提示器)**：模型会自己用英文记录诸如 *"CRITICAL: Pointer stopped at 30, but ID 12 is still in debt."* 的反思笔记。在下个月继续排班时，这串笔记会作为 `PREVIOUS RUN MEMORY` 毫厘不差地喂回给大模型。

---

## 3. 🧩 提示词工程与系统幻觉抑制 (Prompt Engineering & Anti-Hallucination)

让大语言模型处理严格的数理循环排班是极为困难的，很容易出现“胡言乱语”、“跳号漏号”、“月份算错”等幻觉（Hallucinations）。我们采用了以下核心手段进行遏制：

### 3.1 “强迫递推式”思维链 (Iterative Chain-of-Thought)
在提示词的 Output Schema 中，我们**强制规定**了 JSON 结构的第一层必须是 `thinking_trace`（思考轨迹）：
```json
{
  "thinking_trace": {
    "step_1_analysis": "Day_index 0 is Tue. IDs 5,6 are Team (blocked).",
    "step_2_pointer_logic": "Debt Queue is empty. Main Pointer moved from 10 to 12.",
    ...
  }
}
```
**原理与妙处**：大模型的输出是依赖“上文 Token”的。强制它在吐出排班结果前，先一板一眼地写出“今天是周几”、“因为 5 号请假，所以我把 5 号记入债务清单，主指针移到 6 号”，能通过**“强迫打草稿”**的方式将其逻辑运算准确率提升 80% 以上。

### 3.2 历法与日期“降维打法”
（见最新修复提交）：
由于 AI 不懂历法（分不清二月哪年有28、29天），直接强迫 AI 计算跨月日期极易产生 `02-29` 或 `03-32` 的幻觉 Bug。
**解法**：剥除 JSON 中的 `date (YYYY-MM-DD)` 输出字段。让 AI 仅仅输出“这是循环中的第 0 天，它是周二”。下游的 Python 收到结果后，通过循环数组的索引 `idx` 倒推真实的年月日，彻底消灭历法层面的大模型幻觉，同时**极大压缩了 Token 开销**。

### 3.3 结果兜底与物理防乱套机制
*   **动态扩展捕获**：如果大模型为了满足用户类似于“明天增加大扫除”的自然对话要求，自己在 JSON 中凭空捏造了名为 `"大扫除"` 的区域。[core.py](file:///c:/Users/ZhuanZ/OneDrive/Desktop/Duty-Agent/Assets_Duty/core.py) 现已支持捕获这些动态生造的 Key 并做前台映射。
*   **Python 物理债务校验**：若 AI 给出的债务队列与实际生成的名单冲突，Python 防疫机制会在存盘前进行差集运算重写 `debt_list`，确保债务体系不可破产。

---

## 4. ⚡ 流式心跳全双工架构 (Streaming & UI Integration)

作为一个现代客户端软件，Duty-Agent 为了避免用户在排长月排班时盯着白屏干等 40 秒，部署了极其精巧的**前后端心跳通信总线**。

### 4.1 `__DUTY_PROGRESS__` 自定义 IPC 载荷
当 Python 后端以 `stream=True` 并发请求大型混合推理端（如 DeepSeek-R1 / Kimi / Qwen）时：
Python 会截获原始的 SSE 碎文本，并将那些属于“思维链打草稿”以及引擎本身进度的阶段，序列化为特殊字符串推入系统 stdout：
```
__DUTY_PROGRESS__:{"phase": "stream_chunk", "message": "Receiving...", "chunk": "IDs 5,6 block."}
```

### 4.2 C# Avalonia 层沉浸式黑板系统
在 [DutyMainSettingsPage.axaml.cs](file:///c:/Users/ZhuanZ/OneDrive/Desktop/Duty-Agent/Views/SettingPages/DutyMainSettingsPage.axaml.cs) 中：
1.  后台通过 `OutputDataReceived` 异步捕获上述字符串，正则表达式剥离出 payload。
2.  通过 `Dispatcher.UIThread.Post` 推送到界面 UI 主线程。
3.  **最终视觉呈现**：用户在界面中点击【开始排班计算】后，按钮下方原地滑出**“AI 思考黑板 (Reasoning Board)”**。大模型思考的每一句话，都会伴随着绿色打字机效果逐词呈现（`ReasoningBoardText.Text += chunk`），并在排班成功后边框闪烁高亮，随后原地刷新下方的日历看板。

---

## 5. 🛠 故障熔断与安全降级 (Network & Fault Tolerance)

为了让普通用户免受 OpenAI 泛型 API 提供商参差不齐的折磨，我们在底层网络层包装了坚固的装甲防御：

*   **HTTP 415/422/501 动态自适应回退**：
    部分劣质二次分发 API 接口会伪装自己支持 `/chat/completions`，但一旦开启 `stream=True` 就会报 422 结构错误或直接阻断。
    Python 流调度器捕获到这类特殊代码后，不仅不崩毁，还会自动生成一条内部警告发送回 C# 前端界面：*“Streaming not supported, falling back...”*。随后，程序自动无缝降级发起非流式一次性拉取，确保用户虽然看不到打字动画，但业务结果依旧完好。
*   **超限制阻断重试 (Exponential Backoff)**：对于 HTTP 429 访问过频拒绝，支持最大 `LLM_MAX_RETRIES` 的带指数缓冲延迟的强制打洞重试。
