# Duty-Agent 后端提示词逻辑 - Bug 报告与重构方案

> 记录时间：2026-04-05
> 状态：方案已澄清，实施中

---

## 一、已知 Bug 清单

### Bug 1：`previous_context` 参数形同虚设（功能缺失）
**文件**: `build_prompt.py` L83-85，`single_pass_executor.py` L89

**问题**：`previous_context` 作为参数传入 `build_prompt_messages` 后立即被 `del` 删除，`params_list` 中从未追加。调用方也永远传空字符串。

**影响**：多轮对话记忆功能从未生效，但不影响当前单次执行。

**修复方向**：删除这个死代码参数，或真正实现多轮上下文注入（本次重构中实现）。

---

### Bug 2：`inactive` 模块激活逻辑错误
**文件**: `build_prompt.py` L9-12, L112-114

**问题**：
```python
if is_module_active("inactive", instruction, bool(inactive_ids)):
```
`data_present = bool(inactive_ids)`，仅当有过期人员时才传入 `True`。如果 `inactive_ids = [5]`（张三已请假），用户没特别提，关键词未命中，模块**不会激活**，模型不知道这些人不可用。

**影响**：核心数据正确性，直接影响排班合法性。

**修复方向**：`inactive_ids` 非空时无条件激活，与关键词无关。

---

### Bug 3：`multi_day` 模块激活条件硬编码 `False`
**文件**: `build_prompt.py` L116-117

**问题**：`data_present` 被硬编码为 `False`，只能靠关键词激活。

**修复方向**：移除该冗余模块，语义归并到 `[actions]` 或 `[state_diff]` 中。

---

### Bug 4：Agent4 缺少 `inactive_ids` 字段
**文件**: `multi_agent/executor.py` L281-295

**问题**：Agent4 的 payload 用 `active_ids` 但缺少 `inactive_ids`，无法在 prompt 中明确告知模型禁用人员。

**修复方向**：在 Agent4 的 payload 中加入 `inactive_ids` 字段。

---

### Bug 5：`ctx.config` 就地修改
**文件**: `single_pass_executor.py` L59-60

**问题**：通过引用修改了 `ctx.config` 共享状态，有隐蔽副作用风险。

**修复方向**：使用 `dict(ctx.config)` 复制后再修改。

---

### Bug 6：`normalize_multi_area_schedule_ids` 中 `area_per_day_counts` 被静默丢弃
**文件**: `postprocess.py` L165-166，`single_pass_executor.py` L112-117

**问题**：`area_per_day_counts` 传入后直接 `del` 丢弃，不验证 AI 是否为每个区域分配了正确数量的人。

**修复方向**：在解析器中实现验证逻辑，发现不匹配时抛异常让 AI 重试。

---

## 二、架构原则（已确认）

| 问题 | 结论 |
|------|------|
| 统一输出规范 | INI 纯文本（`[areas]` / `[schedule]` / `[state]`），不在这里引入 JSON Schema |
| Python Tool 调用方式 | LLM tool call（OpenAI function calling 风格） |
| 重构范围 | 标准模式（single_pass）重构 + Multi-Agent 共享同一 INI 解析器（不动 Multi-Agent 架构本身） |
| 三维评估分工 | Python 判人数比；AI 判区域周期性 + 逻辑深度 |
| 澄清交互 | AI 输出 `AMBIGUOUS: ...`，后端捕获推给前端，用户回复后继续 |
| Global Rebuild | AI 直接完成，不调 Python 填充器 |
| 三种模式 | ST / Agents / Extra，共用同一 INI 解析器 |

---

## 三、实施阶段（底座优先，每阶段可独立验证）

---

### 阶段 0：统一 INI 解析器（Foundation）

**目标**：所有模式（ST / Agents / Extra）共用同一个解析器，零逻辑重复。

**新建文件**：`Assets_Duty/parsers/ini_parser.py`

**解析器职责**：
1. 解析 `[areas]` → `{alias: area_name}` 映射
2. 解析 `[schedule]` → `[{date, alias: [ids], ...}, ...]` 列表，按日期升序
3. 解析 `[state]` → `{pointer, debt: {id: count}, credit: {id: count}, consumed_credit: [ids]}`
4. 检测并提取 `AMBIGUOUS: ...` 行
5. 验证每个 area 的 ID 数量是否符合 `area_per_day_counts`
6. 验证无重复 ID、无未知 ID、无 inactive ID
7. 发现违规时抛出结构化异常（含行号和原因）

**API 设计**：
```python
class IniParseError(Exception):
    section: str       # e.g. "schedule"
    line: int           # 行号
    reason: str         # e.g. "duplicate_id", "unknown_id", "count_mismatch"
    raw_line: str       # 原始行

@dataclass
class ParsedSchedule:
    areas: Dict[str, str]                           # alias -> area_name
    entries: List[ParsedEntry]                      # 按日期升序
    state: Optional[ParsedState]
    ambiguous: Optional[str]                        # AMBIGUOUS: 后的文本

@dataclass
class ParsedEntry:
    date: str          # "2026-04-06"
    aliases: Dict[str, List[int]]  # alias -> [1001, 1002]
    note: str

@dataclass
class ParsedState:
    pointer: int
    debt: Dict[int, int]        # id -> count
    credit: Dict[int, int]
    consumed_credit: List[int]
```

**工作内容**：
- [ ] 新建 `parsers/__init__.py`
- [ ] 新建 `parsers/ini_parser.py`，实现上述 API
- [ ] 新建 `parsers/test_ini_parser.py`，覆盖边界 case（重复ID、未知ID、格式错误、空section等）

**验收标准**：
- 所有边界 case 有明确报错（section + line + reason）
- ST / Agents / Extra 三个模式的解析路径都走这个模块

---

### 阶段 1：Bug 修复 + 代码清理

**目标**：修复已知的正确性问题，不改变提示词语义，为阶段 2 打好干净的基础。

**工作内容**：
- [ ] Bug #2：修复 `inactive` 激活逻辑（`is_module_active` 新签名，或单独处理）
- [ ] Bug #5：修复 `ctx.config` 原地修改（改用 `dict()` 复制）
- [ ] Bug #6：`ini_parser` 中实现 `area_per_day_counts` 验证（已在阶段 0 覆盖）
- [ ] Bug #4：Agent4 payload 补充 `inactive_ids`
- [ ] Bug #1：处理 `previous_context`（删除参数 + 更新调用方）
- [ ] Bug #3：移除 `multi_day` 冗余模块（`is_module_active` 调用 + `PROMPTS` 中的 `rule_multi_day`）

**验收标准**：
- `git diff` 显示只有逻辑修复，无功能变更
- 现有测试全部通过（如果有测试）

---

### 阶段 2：标准模式重构 - 底座（Tool Call 接口 + 路由骨架）

**目标**：建立 ST 模式的执行骨架：LLM tool call 接口 + 三维路由（Python判人数比 / AI判深度 / 静默流）。

**新建文件**：`Assets_Duty/tool_dispatcher.py`

**Tool Dispatcher 职责**：
```python
class ToolDispatchResult(Enum):
    CONTINUE = "continue"      # 继续对话，AI 继续写
    COMPLETE = "complete"      # 排班完成，解析 INI
    AMBIGUOUS = "ambiguous"    # 需要用户澄清
    REBUILD = "rebuild"        # 触发全局重建

@dataclass
class ToolDispatchResult_:
    action: ToolDispatchResult
    payload: Any               # COMPLETE 时为 ParsedSchedule，AMBIGUOUS 时为问题文本
    reasoning: str              # AI 的推理说明（用于调试）
```

**路由决策逻辑**（`tool_dispatcher.py`）：
```
1. 接收 AI 的当前 INI 输出（可能是 partial）
2. 尝试解析：
   - 若有 AMBIGUOUS 标记 → 返回 AMBIGUOUS
   - 若 [schedule] 完整且 [state] 完整 → 返回 COMPLETE
   - 若 [schedule] 不完整 → 进入三维评估：
     a. Python 侧：计算影响人数比，超过阈值 → 返回 REBUILD
     b. 调起下一步 prompt，让 AI 评估深度 → AI 返回 Depth + CONTINUE 或 REBUILD
   - CONTINUE → 返回 partial，继续让 AI 追加

3. Tool 响应（给 LLM）：
   - CONTINUE：追加 prompt 说明当前状态 + 剩余 slot
   - AMBIGUOUS：返回澄清问题
   - REBUILD：追加 prompt 说明全面重建
   - COMPLETE：标记完成，进入结算
```

**工作内容**：
- [ ] 新建 `tool_dispatcher.py`
- [ ] 在 `single_pass_executor.py` 中引入 dispatcher
- [ ] 实现人数比熔断逻辑
- [ ] 实现 AMBIGUOUS 捕获和转发（MCP/WebSocket 通道）
- [ ] 实现 REBUILD 路径（AI 直接完成，不调 Python 填充器）

**验收标准**：
- 一个最小可用的 ST 执行路径能跑通（不需要真的调 LLM，用 mock）
- `AMBIGUOUS` / `REBUILD` / `COMPLETE` / `CONTINUE` 四种状态都能触发

---

### 阶段 3：标准模式重构 - 提示词工程

**目标**：将干净的标准模式提示词落地，替换 `build_prompt.py` 和 `prompt_config.py`。

**工作内容**：
- [ ] 重写 `prompt_config.py`：
  - System Prompt 模板（身份定义 + 绝对规则 + INI 格式约束 + 账本快照）
  - User Instruction 模板（仅含用户自然语言指令）
  - `[actions]` 格式说明（替代原来的模块激活逻辑）
  - `[state_diff]` 格式说明（替代原来的绝对 [state]）
- [ ] 重写 `build_prompt.py`：
  - 删除 `is_module_active` 及其相关冗余代码
  - 删除 `area_names` / `area_per_day_counts` / `previous_context` 的 `del` 语句
  - 新增 `[actions]` 生成逻辑（由 Python 从 instruction 推断）
  - System / User 严格分离注入
- [ ] 更新 `single_pass_executor.py`：对齐 INI 解析器和 tool dispatcher

**验收标准**：
- prompt 输出可读、可审计（System 静态、User 动态、账本快照单独注入）
- `ini_parser` 能解析新格式的所有输出

---

### 阶段 4：Multi-Agent 模式对接

**目标**：让 Multi-Agent 各 Agent 使用同一 INI 解析器（不动 agents 架构）。

**工作内容**：
- [ ] `multi_agent/executor.py` 中的 `finalize_multi_agent_run` 对接 `ini_parser`
- [ ] `multi_agent/settlement.py` 中的 `restore_schedule` 对接 `ini_parser`
- [ ] 验证 `multi_agent/validators.py` 的验证结果与解析器一致
- [ ] `multi_agent/prompts.py` 中的各 Agent prompt 确认使用 INI 输出

**验收标准**：
- Multi-Agent 模式下 `restore_schedule` 能正确处理 Agent6 的输出
- 解析器错误能正确反映到 agent_summary 中

---

### 阶段 5：收尾与文档

**工作内容**：
- [ ] 更新 `docs/prompt-refactor-plan.md`，标注各阶段完成状态
- [ ] 确认无遗留死代码
- [ ] 补充 `ini_parser.py` 的 docstring 和类型注解

---

## 四、阶段依赖关系

```
阶段0（INI解析器） ──→ 阶段1（Bug修复）
                            │
                            ↓
                    阶段2（Tool骨架） ──→ 阶段3（提示词工程）
                                               │
                                               ↓
                                  阶段4（Multi-Agent对接）
                                               │
                                               ↓
                                        阶段5（收尾文档）
```

**关键约束**：
- 阶段 0 必须最先完成，因为所有后续阶段都依赖解析器
- 阶段 2 和阶段 3 可并行（骨架和提示词可分头写）
- 阶段 4 在阶段 3 完成后进行

---

## 五、当前状态

| 阶段 | 状态 |
|------|------|
| 阶段0：统一INI解析器 | 待开始 |
| 阶段1：Bug修复+清理 | 待开始 |
| 阶段2：Tool Call骨架 | 待开始 |
| 阶段3：提示词工程 | 待开始 |
| 阶段4：Multi-Agent对接 | 待开始 |
| 阶段5：收尾文档 | 待开始 |
