# Duty-Agent 后端提示词逻辑 - 完整工作台

> 记录时间：2026-04-05
> 状态：重构进行中，双向协议设计已确定，待实现 INI 解析器

---

## 一、当前代码结构

```
Duty-Agent/
├── run_desktop_dev.bat       # 开发模式（双击直接运行，无需打包）
├── build_desktop.bat         # 打包脚本（PyInstaller）
└── Assets_Duty/
    ├── core.py               # FastAPI 后端（已有 /app 静态文件托管）
    ├── desktop/
    │   ├── __init__.py
    │   ├── launcher.py       # 入口：启动后端 + pywebview 窗口
    │   ├── backend_manager.py # 子进程管理 + 端口检测
    │   └── desktop.spec      # PyInstaller 打包配置
    ├── orchestrator/         # Orchestrator Agent 架构（阶段4）
    │   ├── __init__.py
    │   ├── context.py        # OrchestratorContext, TimeWindow, Hint
    │   ├── decomposer.py     # 时间窗口动态切分
    │   ├── prompt.py         # Orchestrator + TimeWindow Agent prompts
    │   ├── aggregator.py      # INI 片段汇总
    │   ├── hints_handler.py   # Hints 分类与分发
    │   └── executor.py       # 主循环入口
    ├── tool_loop/
    │   ├── executor.py       # 轮询执行器（ST 模式）
    │   ├── ini_handler.py    # INI 解析器（统一仲裁层）
    │   └── tool_prompt.py    # 轮询提示词
    └── multi_agent/
        ├── executor.py        # Agents 模式执行器
        ├── settlement.py      # 结算
        └── validators.py     # 验证器
```

---

## 二、已知 Bug 清单

### Bug 1：`previous_context` 参数形同虚设
- **文件**: `build_prompt.py` L83-85，`single_pass_executor.py` L89
- **问题**: 参数传入后立即被 `del` 删除，调用方永远传空字符串
- **状态**: ✅ 已修复（移除 `del`，将 `previous_context` 内容注入 system_content 末尾）

### Bug 2：`inactive` 模块激活逻辑错误
- **文件**: `build_prompt.py` L112-114
- **问题**: `data_present = bool(inactive_ids)`，导致过期人员可能不被通知给 AI
- **状态**: ✅ 已修复（`bool(inactive_ids)` 本身正确，问题在于 `data_present=False` 时也激活；现已对齐）

### Bug 3：`multi_day` 模块关键词过于宽泛
- **文件**: `build_prompt.py` L116-117
- **问题**: 关键词"到"过于宽泛，`rule_multi_day` 内容冗余
- **状态**: ✅ 已修复（删除 `multi_day` 模块）

### Bug 4：Agent4 缺少 `inactive_ids` 字段
- **文件**: `multi_agent/executor.py` L281-295
- **问题**: Agent4 的 payload 无禁用人员信息
- **状态**: ✅ 已修复（补充 `inactive_ids: snapshot.inactive_ids`）

### Bug 5：`ctx.config` 就地修改
- **文件**: `single_pass_executor.py` L59-60
- **问题**: 直接修改 `ctx.config` 共享状态引用
- **状态**: ✅ 已修复（`load_config()` 在 `api_key` 读取之后调用，不再覆盖）

### Bug 6：`area_per_day_counts` 被静默丢弃
- **文件**: `postprocess.py` L165-166
- **问题**: 传入后 `del` 丢弃，不验证区域人数约束
- **状态**: ✅ 已修复（改为文档注释保留，标注为未来扩展预留）

---

## 三、架构决策（已确认）

| 决策项 | 结论 |
|--------|------|
| 统一输出规范 | INI 纯文本，需重新设计以适配 Python Tool Call |
| Python Tool 调用方式 | LLM tool call（OpenAI function calling 风格） |
| 重构范围 | 标准模式（single_pass）重构 + Multi-Agent 共享同一 INI 解析器 |
| 三维评估分工 | Python 判人数比；AI 判区域周期性 + 逻辑深度 |
| 澄清交互 | AI 输出 `AMBIGUOUS: ...`，后端推给前端，用户回复后继续 |
| Global Rebuild | AI 直接完成，不调 Python 填充器 |
| 三种模式 | ST / Agents / Extra，共用同一 INI 解析器 |
| **LLM 指令协议** | **废弃 `[actions]`，改用 `@keep` / `@drop` / `@replace` / `@finalize` 特殊指令** |
| **轮询控制权** | **Python 自己模拟推进 debt/credit/pointer，LLM 只管引用和调整批次** |
| **动态提示词** | **`hints_on` 控制是否注入指令提示；关闭时 LLM 不知指令存在，Python 静默忽略** |
| **consumed_credit** | **删除，不作为独立字段** |

---

## 四、三种模式架构

| 模式 | 特点 |
|------|------|
| **ST（Single Tool）** | 极致省 Token，AI 通过 function call 调 Python 填充器，最终 AI 完成全部输出 |
| **Agents** | 分工协作，各 Agent 可调用同一解析器 |
| **Extra** | 本地平台专用 |

---

## 五、实施阶段（草案，待 INI 格式确定后更新）

```
阶段0（统一INI解析器）──→ 阶段1（Bug修复）
                                ↓
                        阶段2（Tool Call骨架）
                                ↓
                        阶段3（提示词工程）
                                ↓
                        阶段4（Multi-Agent对接）
                                ↓
                        阶段5（收尾文档）
```

> ⚠️ 阶段 0 需先确定新的 INI 输出规范，才能开始。

---

## 六、双向协议设计（已确定）

### 6.1 核心思想

**轮询循环 + 批次引用**：Python 每次轮询返回 INI 格式的完整上下文（包含状态摘要和未安排的班次），LLM 引用批次编号（`[[#week1]]`）来调整批次，无需重复发送完整列表。

**Python 模拟推进**：债务/积分/指针由 Python 自己计算，LLM 不直接操作状态，只通过特殊指令引用和调整批次。

**两阶段解析**：
1. **阶段一**：`[[#batch]]` + `[schedule]` → Python 解析批次结构，执行 `@keep`/`@drop`/`@replace`
2. **阶段二**：`[state]` → Python 计算实际落表后的状态，写入权威值

AI 的 `[state]` 是**参考**，Python 的 `[state]` 是**结果**。不一致时以 Python 为准。

---

### 6.2 状态模型（state.json）

```json
{
  "schedule_pool": [...],
  "debt_counts": {"1004": 2, "1002": 1},
  "credit_counts": {"1001": 2},
  "last_pointer": 5
}
```

- `debt_counts`：欠任务计数，同一人可欠多次（`1004: 2` 表示欠 2 次）
- `credit_counts`：奖励计数，同一人可多次奖励
- `last_pointer`：轮询指针在 `all_ids` 列表中的索引
- **债务优先**：若某人同时有 debt 和 credit，credit 被清除（`resolve_debt_credit_conflicts`）
- **指针推进**：指针在 `all_ids` 中循环移动，经过 credit 人时跳过（consume credit），经过 debt 人时分配任务（clear debt）

---

### 6.3 Python → LLM 返回格式

每次轮询 Python 返回完整 INI 模板：

```ini
[state]
round = 3
pointer = 7
debt = 1004*2 1002
credit = 1001*2

[[#week1]]
04-01 = A:1001 1002 | B:1003 1004
04-02 = A:1005 1006 | B:1007 1008
  ↓
A区 人员3 ✓ 展开结果
A区 人员7 ✗ 债务=0无信用，跳过，改用5

[[#week1-drop]]
04-02 = B:1007 1008

[remaining]
04-03 = A:?
04-03 = B:?

; 可用人员:
;   A区 人员3 信用+1
;   A区 人员7 空闲
```

**说明**：
- `[[#batchname]]` 标注文本可包含展开结果或错误原因（供前端渲染，不影响 Python 解析）
- `[[#batchname-drop]]` 标注本批次中已失效的条目
- `[remaining]` 列出所有未安排的班次（Python 每次重新计算）
- `;` 注释行供 LLM 理解上下文，不执行

---

### 6.4 LLM → Python 特殊指令

LLM 输出 INI 时，可在批次标注行或单独一行写特殊指令：

| 指令 | Python 行为 |
|------|-------------|
| `@keep(#batch [slot])` | 保留批次内指定 slot（未提及的 slot 释放回 remaining） |
| `@drop(#batch)` | 丢弃整批，slot 全部释放回 remaining |
| `@replace(#batch SLOT=NEW_ID)` | 替换批次内指定 slot 的人员 |
| `@finalize` | 写入 state.json，结束轮询 |

**示例**：

```ini
[[#week1]]
04-01 = A:1001 1002 | B:1003 1004

@keep(#week1 04-01 A)   ; 保留 04-01 A区的 slot，其余释放
@drop(#week2)            ; 丢弃 week2 整批
@replace(#week1 04-02 B=1009)  ; 把 week1 04-02 B 区换成 1009
@finalize
```

**解析规则**：
- Python 先解析所有特殊指令，按顺序执行
- `@keep` 保留指定 slot，其余释放回 `[remaining]`
- `@drop` 整批释放
- `@replace` 替换后继续
- 执行完后，Python 计算实际的 `[state]`，覆盖 LLM 的声明
- Python 返回权威的完整模板（包含 Python 计算的 state）

---

### 6.5 动态提示词控制

```json
// settings.json
{
  "polling": {
    "hints_on": true,
    "max_rounds": 15
  }
}
```

| 设置 | 前端注入 | LLM 行为 | Python 行为 |
|------|---------|---------|-----------|
| `hints_on = true` | 注入 `@keep`/`@finalize` 提示词 | 看到提示词 → 写指令 | 解析并执行 |
| `hints_on = false` | 无提示词 | 不知道指令 → 不写 | 忽略（不报错） |

**设计原则**：提示词关了 = LLM 不知道 = 自然不会写。LLM 鬼使神差写了 = 自己猜的 = Python 静默忽略。零心智负担。

---

### 6.6 轮询终止条件

满足任一即终止：
1. LLM 输出 `@finalize`
2. `[remaining]` 为空（所有班次已安排）
3. 轮次达到 `max_rounds` 上限

---

### 6.7 ST 模式完整交互流程

```
第1轮：Python 返回初始模板（round=1, remaining=全部班次）

LLM 输出 INI + 特殊指令
  ↓
Python 解析：
  1. 解析 [[#batch]] 批次结构
  2. 执行 @keep/@drop/@replace 指令
  3. 验证 [schedule] 中每条
  4. 落表到 schedule_pool
  5. 计算实际 [state]（覆盖 LLM 声明）
  6. 重新计算 [remaining]
  7. 返回完整模板（round+1）
  ↓
第2轮：LLM 读新模板，输出下一批批次 + 指令

... 循环直到终止条件满足

最终：Python 写入 state.json，结束
```

---

### 6.8 待确认问题

- [ ] `area_per_day_counts` 区域人数约束：Python 硬验证还是 AI 软约束？
- [ ] LLM 引用错误批次号：Python 静默忽略还是警告？
- [ ] 轮询上限 `max_rounds` 默认值：15 是否合理？

---

### 6.9 两种执行流

| 模式 | 流程 |
|------|------|
| **Single Pass** | AI 一次性输出完整 INI → Python 解析验证 → 更新 state |
| **Multi-Agent** | Agent1(锚) + Agent2(账) + Agent3(规则) → Barrier1 → Agent4(优先级) + Agent5(指针) → Barrier2 → Agent6(组装) → 结算 |

ST 模式重构后，两种模式共享同一 INI 解析器。

---

## 七、实施阶段

```
阶段0（统一INI解析器）──→ 阶段1（Bug修复）──→ 阶段2（Tool Call骨架）──→ 阶段3（提示词工程 + hints_on 注入）──→ 阶段4（Orchestrator 架构）──→ 阶段5（收尾文档）

状态：
  阶段0 ✅ 完成（tool_loop/ini_handler.py 完全重写）
  阶段1 ✅ 完成（Bug #1-6 全部修复）
  阶段2 ✅ 完成（tool_loop/executor.py 已重构并集成到 engine.py；llm_transport.py 支持 tools 参数）
  阶段3 ✅ 完成（tool_loop/tool_prompt.py 系统提示词 + hints_on 注入）
  阶段4 ✅ 完成（orchestrator/ 架构：Orchestrator Agent + TimeWindow Agent + 统一仲裁层）
  阶段5 ⏳ 待启动

附加修复（本次会话）：
  ✅ Bug #7: `@finalize` 被优先处理，导致所有 `@keep`/`@drop`/`@replace` 被跳过
     文件: ini_handler.py `apply_special_commands`
     修复: 将 `@FINALIZE` 移至所有批次处理之后
  ✅ Bug #8: `compute_remaining_units` 因 area name 不匹配导致 remaining 不清空
     文件: ini_handler.py `compute_remaining_units`
     修复: 比较只按 date，忽略 area name 差异（LLM 可自由声明 [areas]）
  ✅ Bug #9: `polling` 字段未被 `patch_config` 保留，导致 hints_on 无法持久化
     文件: state_ops.py `_normalize_persisted_config`, `_persisted_body`
     修复: 将 `polling` 加入规范化流程并在 patch 时保留
  ✅ Bug #10: `single_pass_executor.py` 删除了关键变量导致 NameError
     文件: single_pass_executor.py
     修复: 恢复 `load_roster`/`load_state`/`input_data`/`instruction`/`trace_id` 定义
  ✅ Bug #11: LLM tool call 提取失败（qwen3.5 流式返回空 content）
     根因: qwen3.5:9b 的 streaming 模式无法正确流式传输 tool_calls，content 全为空
     文件: llm_transport.py, verify_ollama.py
     修复:
       1. streaming 模式若 content 为空且有 tools 参数，自动 fallback 到 non-stream 模式
       2. non-stream 模式中优先提取 tool_calls（不在 message.content 中）
       3. 为 Ollama 请求自动注入 `think: False` 防止模型跳过 tool_calls
       4. verify_ollama.py 同步实现相同 fallback 逻辑
     验证: qwen3.5:9b 现在每轮都能正确调用 fill_schedule，1 轮完成 2 天排班
  ✅ Orchestrator 架构（阶段4）:
     新增 orchestrator/ 模块：context.py, decomposer.py, prompt.py, aggregator.py, hints_handler.py, executor.py
     新增 orchestrator_runtime_mode，Orchestrator Agent + TimeWindow Agent 并行模式
     时间窗口动态切分：total_slots < 7 → Orchestrator 直接做；≥7 → 切 N 个窗口并行
     execution_profiles.py 新增 orchestrator 模式支持
     engine.py 新增 run_orchestrator_schedule 入口
     与 ST 模式共享 ini_handler.py 仲裁层
  ✅ 桌面应用（pywebview 套壳）:
     新增 desktop/ 模块：backend_manager.py, launcher.py, __init__.py, desktop.spec
     desktop/launcher.py：启动后端（core.py --server） + 打开 pywebview 窗口
     desktop/backend_manager.py：子进程管理，stdout 捕获端口，超时 15s
     run_desktop_dev.bat：开发模式直接运行（无需打包）
     build_desktop.bat：PyInstaller 打包脚本，输出 dist/DutyAgent/DutyAgent.exe
     pywebview Windows 版使用系统 Edge WebView2，无需额外依赖
```

---

## 八、桌面应用

```
Duty-Agent/
├── run_desktop_dev.bat    # 开发模式（双击直接运行）
├── build_desktop.bat      # 打包脚本
└── Assets_Duty/
    ├── core.py            # FastAPI 后端（已有 /app 静态文件托管）
    └── desktop/
        ├── __init__.py
        ├── launcher.py    # 入口：启动后端 + pywebview
        ├── backend_manager.py  # 子进程 + 端口检测
        └── desktop.spec   # PyInstaller 打包配置
```

**依赖**：`pip install pywebview pyinstaller`

**启动流程**：
1. `run_desktop_dev.bat` → `python desktop/launcher.py`
2. `launcher.py` 启动 `core.py --server --port 8765` 子进程
3. `backend_manager.py` 从 stdout 捕获 `__DUTY_SERVER_PORT__:xxxx` 得到端口
4. `pywebview.create_window(url=f"http://127.0.0.1:{port}/app")` 打开窗口
5. 窗口关闭 → `stop_backend()` 清理子进程

**打包**：`build_desktop.bat` → `Assets_Duty/dist/DutyAgent/DutyAgent.exe`

---

## 九、参考资料

- `docs/prompt-refactor-plan.md` - 完整重构计划文档
- `Assets_Duty/build_prompt.py` - 提示词组装
- `Assets_Duty/prompt_config.py` - 提示词模板和关键词注册
- `Assets_Duty/single_pass_executor.py` - 标准模式执行器
- `Assets_Duty/multi_agent/executor.py` - Agents 模式执行器
- `Assets_Duty/postprocess.py` - 后处理
- `Assets_Duty/state_ops.py` - 状态管理和账本操作
- `Assets_Duty/tool_loop/ini_handler.py` - 轮询 INI 处理器（新）
- `Assets_Duty/tool_loop/executor.py` - 轮询执行器（新）
- `Assets_Duty/tool_loop/tool_prompt.py` - 轮询提示词（新）

---

## 九、UI 与 FastAPI 未对齐功能报告

> 记录时间：2026-04-05（上次）→ 2026-04-05（本次更新）
>
> 分析范围：`duty-agent-ui/src/pages/SettingsPage.vue`（UI 前端） ↔ `Assets_Duty/routers/` + `Assets_Duty/state_ops.py`（FastAPI 后端）
>
> **本次更新说明**：重新核查所有 P0/P1 问题，确认实际修复进度。结论：**0 个新文件被创建**，后端数据层小幅改进（字段能读），但所有 API 链路（路由/白名单/schema）和 UI 逻辑（加载/保存/按钮）**全部未打通**。

### 9.1 严重程度总览（更新版）

| 级别 | 功能 | 根因 | 变化 |
|------|------|------|------|
| 🔴 P0 | 自动运行（8项）+ 通知设置（3项）配置 | 完全无 API 端点 + UI 无读写逻辑 | 未变化 |
| 🔴 P0 | 方案预设 UI — 新建/复制按钮 | UI 有按钮完全无事件处理 | 未变化 |
| 🔴 P0 | 方案预设 UI — 执行模式/Agent顺序/模型画像 Select | 后端 schema 缺字段 + patch_config 白名单过窄 + UI 无 PATCH 调用 | 未变化 |
| 🟡 P1 | AI 工具配置（工具服务器列表） | 完全没有 API 端点，store 绕过 FastAPI 直连 MCP | 未变化 |
| 🟡 P1 | planMode 值不匹配（新增发现） | UI 用 `incremental_small`（下划线），后端用 `incremental-small`（连字符） | 🆕 新发现 |
| 🟢 | duty_rule 长期规则 | ✅ GET + PATCH 均正常工作 | 未变化 |
| 🟢 | config/snapshot/roster 基础数据 GET | ✅ 均正常工作 | 未变化 |
| 🟢 | 后端数据层能返回三个模式字段 | ✅ `load_config()` 填充 `orchestration_mode` / `multi_agent_execution_mode` / `model_profile` | 🆕 已改进 |
| 🟢 | `engine_info()` 暴露支持值列表 | ✅ 返回所有支持的 profiles 和 modes | 🆕 已改进 |

**整体结论**：后端数据层有微小改进（能填充和暴露三个模式字段），但 API 链路和 UI 逻辑**全部未实现**。P0 × 3，P1 × 1（共 4 个问题需处理）。

---

### 9.2 自动运行 Tab — 完全断裂（P0，未变）

以下 9 个 UI 变量在 `SettingsPage.vue` 中有定义，但**从未从后端加载，也从未保存回去**：

| 配置项 | UI 变量 | 后端 host-config.json 字段 | 状态 |
|--------|---------|---------------------------|------|
| 自动排班开关 | `autoRunEnabled` | `auto_run_mode` (Off/Weekly/Monthly/Custom) | 🔴 断裂 |
| 触发策略 | `autoRunMode` | `auto_run_mode` | 🔴 断裂 |
| 每周日期 | `autoRunDay` | `auto_run_parameter` | 🔴 断裂 |
| 执行时间-时 | `autoRunHour` | `auto_run_time` (HH:MM) | 🔴 断裂 |
| 执行时间-分 | `autoRunMinute` | `auto_run_time` (HH:MM) | 🔴 断裂 |
| 触发时通知 | `triggerNotification` | `auto_run_trigger_notification_enabled` | 🔴 断裂 |
| 提醒开关 | `reminderEnabled` | `duty_reminder_enabled` | 🔴 断裂 |
| 提醒时间 | `reminderTimes` | `duty_reminder_times` (List[str]) | 🔴 断裂 |
| 显示时长 | `notificationDuration` | `notification_duration_seconds` | 🔴 断裂 |

**后端现状（未变）：**

`state_ops.py` L743-762 定义了完整的 Host Config 模型，包含上述全部字段，`load_host_config()` / `save_host_config()` 均已实现，但 **FastAPI 完全没有暴露 `/api/v1/host-config` 端点**。

**UI 问题（未变）：**

```typescript
// SettingsPage.vue L91-117 — 初始化了变量，但 onMounted 从未读取
const autoRunEnabled = ref(false);
const autoRunMode = ref('weekly');
const autoRunDay = ref('1');
const autoRunHour = ref(8);
const autoRunMinute = ref(0);
const triggerNotification = ref(true);
const reminderEnabled = ref(false);
const reminderTimes = ref('07:40, 12:10');
const notificationDuration = ref(8);
```

`onMounted` 只调用了 `api.getConfig()`，从未调用 host-config 接口。

---

### 9.3 通知设置 Tab — 仅 UI 占位（P0，未变）

| 功能 | UI 状态 | 后端字段 | API 端点 | 状态 |
|------|---------|---------|---------|------|
| 通知设置整个 Tab | 占位 UI（"待实现"） | `notification_duration_seconds`, `duty_reminder_*` | ❌ | 🔴 空壳 |

后端 `host-config.json` 有完整字段，但 UI 只有占位卡片。

---

### 9.4 方案预设 UI — 部分改进但仍有 5 处断裂（P0，更新）

**改进部分：** `onMounted` 现已正确加载三个模式字段（SettingsPage.vue L62-66）：

```typescript
// SettingsPage.vue L62-66
orchestrationMode.value = plan.orchestration_mode || 'Incremental';
multiAgentExecutionMode.value = plan.multi_agent_execution_mode || 'Sequential';
modelProfile.value = plan.model_profile || 'balanced';
```

`engine_info()` 也返回了完整的支持值列表（query_service.py L24-28）。

**断裂部分（5 处）：**

| 功能 | 代码位置 | 问题 | 状态 |
|------|---------|---------|------|
| 新建方案按钮 | L205 | 点击无任何事件处理 | 🔴 断裂 |
| 复制当前按钮 | L206 | 点击无任何事件处理 | 🔴 断裂 |
| 执行模式 Select | L223-228 | 变更不触发 PATCH 调用 | 🔴 断裂 |
| Agents 执行顺序 Select | L248-253 | 变更不触发 PATCH 调用 | 🔴 断裂 |
| 模型画像 Select | L260-265 | 变更不触发 PATCH 调用 | 🔴 断裂 |

Save 按钮绑定为空（SettingsPage.vue L272 无方法），UI 有 model 值但无法回写。

---

### 9.5 值不匹配 Bug — planMode ID 错误（🆕 P1）

发现新的值对齐问题：

| 位置 | 值 | 预期值 |
|------|-----|-------|
| UI `planModeOptions` | `incremental_small`（下划线） | `incremental-small`（连字符） |
| 后端 plan preset id | `incremental-small`（连字符） | — |

**后果：** UI 切换到"增量小模型"方案时，后端匹配失败，切换无效。用户无法通过 UI 正确切换到该预设。

来源：`SettingsPage.vue` L41，`state_ops.py` L215。

---

### 9.6 AI 工具 Tab — 架构完整但 API 缺失（P1，未变）

| 功能 | 后端有 | 前端 store 有 | API 端点 | 状态 |
|------|-------|-------------|---------|------|
| MCP 工具服务器启用 | `enable_mcp` | ✅ | ❌ | 🟡 |
| 工具服务器列表管理 | tool_loop 子系统 | ✅ | ❌ | 🟡 |
| 每轮最大调用次数 | tool_loop | ✅ | ❌ | 🟡 |
| 调用超时时间 | tool_loop | ✅ | ❌ | 🟡 |

**更严重：** `aiToolsStore.ts` L84-96 通过原生 `fetch()` 直连 MCP 运行时，**完全绕过 FastAPI**，意味着 AI 工具配置不走统一 API 入口，不受 token 认证保护。

---

### 9.7 Host Config API 层完全缺失（未变）

后端 `state_ops.py` L845-895 有完整的 Host Config 管理函数，但 `routers/` 目录下仅有：

```
Assets_Duty/routers/
├── config.py    # /api/v1/config, /api/v1/snapshot
├── duty.py      # /api/v1/duty/*
└── roster.py    # /api/v1/roster
```

**缺少：`/api/v1/host-config` 路由文件。**

Host Config 包含 20 个字段（自动运行、通知、令牌模式、MCP 开关等），是 UI 自动运行/通知设置/AI 工具三个 Tab 的后端数据源。

---

### 9.8 PATCH 字段白名单过窄（未变）

**文件：** `Assets_Duty/models/schemas.py` L68-74

```python
class DutyBackendConfigPatch(BaseModel):
    expected_version: Optional[int] = None
    selected_plan_id: Optional[str] = None
    plan_presets: Optional[List[DutyPlanPresetModel]] = None
    duty_rule: Optional[str] = None
    # ❌ 缺少: orchestration_mode, multi_agent_execution_mode, model_profile
```

`state_ops.py` L670 的 `patch_config()` 白名单同样只有 4 个字段：

```python
unsupported_keys = sorted(
    str(key)
    for key, value in (patch or {}).items()
    if value is not None
    and key not in {"expected_version", "selected_plan_id", "plan_presets", "duty_rule"}
)
```

这意味着 UI 即使调用 `api.updateConfig()` 传入 `orchestrationMode` / `multiAgentExecutionMode` / `modelProfile`，后端会抛出 `ValueError` 并**静默失败**（UI 层面无法感知）。

---

### 9.9 修复优先级建议（更新版）

| 优先级 | 任务 | 工作量 | 备注 |
|--------|--------|------|--------|
| P0 | 新增 `routers/host_config.py`（GET/PATCH `/api/v1/host-config`） | 中等 | 最关键，9.2/9.3/9.6 共享后端 |
| P0 | UI `onMounted` 加载 Host Config 数据 | 小 | 对应 SettingsPage.vue L91-117 |
| P0 | UI 自动运行 Tab 增加保存逻辑 | 小 | Save 按钮绑定 |
| P0 | 方案预设 Select 增加 PATCH 调用 | 小 | 三个 Select 各自增加 savePlanPreset() |
| P0 | 新建/复制方案按钮增加事件处理 | 小 | SettingsPage.vue L205-206 |
| P0 | `DutyBackendConfigPatch` schema 增加 3 个字段 | 小 | schemas.py L68-74 |
| P0 | `patch_config()` 白名单增加 3 个字段 | 小 | state_ops.py L670 |
| P0 | 修复 `incremental_small` → `incremental-small` 值对齐 | 小 | SettingsPage.vue L41 |
| P1 | 新增 `/api/v1/tool-servers` API 端点（迁移 MCP 配置走 FastAPI） | 中等 | aiToolsStore.ts 去掉原生 fetch |
| P2 | 通知设置 Tab 内容实现 | 小 | |

---

### 9.10 相关文件索引（更新版）

**UI 前端：**
- `duty-agent-ui/src/pages/SettingsPage.vue` — 设置主页，4 个 Tab（部分 P0 未修复）
- `duty-agent-ui/src/components/ai-tools/AiToolsPanel.vue` — AI 工具配置面板（绕过 FastAPI）
- `duty-agent-ui/src/components/ai-tools/ToolServerForm.vue` — 服务器表单
- `duty-agent-ui/src/stores/aiToolsStore.ts` — AI 工具状态管理（🆕 直连 MCP，非 FastAPI）
- `duty-agent-ui/src/types/ai-tools.ts` — AI 工具类型定义
- `duty-agent-ui/src/api/http.ts` — API 客户端（🆕 无 host-config / tool-servers 方法）

**FastAPI 后端：**
- `Assets_Duty/routers/config.py` — `/api/v1/config`, `/api/v1/snapshot`
- `Assets_Duty/routers/duty.py` — `/api/v1/duty/*`
- `Assets_Duty/routers/roster.py` — `/api/v1/roster`
- ❌ `Assets_Duty/routers/host_config.py` — **缺失（需新建）**
- ❌ `Assets_Duty/routers/tool_servers.py` — **缺失（需新建）**
- `Assets_Duty/models/schemas.py` — Pydantic 数据模型（🆕 缺少 3 个 PATCH 字段）
- `Assets_Duty/state_ops.py` — 状态管理（含 Host Config，PATCH 白名单需扩展）
- `Assets_Duty/application/query_service.py` — ✅ `engine_info()` 已返回支持值列表



## 十、ClassIsland 桥接插件方案

> 记录时间：2026-04-05
>
> 目标：新建 `Duty-Agent-ClassIsland-Bridge/` 文件夹，作为独立 ClassIsland 插件，通过 IPC 与独立桌面软件 `Duty-Agent/` 联动。**不修改 `Duty-Agent/` 现有代码。**

### 10.1 架构概览

```
ClassIsland Shell
         │
         ▼
┌────────────────────────────────────────┐
│  Duty-Agent-ClassIsland-Bridge/        │
│  Plugin.cs                             │
│  Services/IpcBridgeService.cs           │
│  Services/NotificationBridge.cs         │
│  Services/ComponentBridge.cs            │
│  Controls/DutyComponent.axaml           │
│  Views/SettingsPage.axaml               │
└────────────────────────────────────────┘
         │  HTTP (localhost:随机端口)
         │  WebSocket (排班实时进度)
         │  元数据文件 (端口/Token/路径)
         ▼
┌────────────────────────────────────────┐
│  Duty-Agent/  (独立桌面软件，不修改)     │
│  Plugin.cs + Assets_Duty/core.py        │
└────────────────────────────────────────┘
```

### 10.2 通信发现机制

独立软件启动后写入元数据文件，桥接插件读取该文件来发现端口和认证信息：

**元数据文件** `.duty-agent-meta.json`：

```json
{
  "version": "0.50.0",
  "port": 0,
  "token_mode": "dynamic",
  "token": "",
  "started_at": "2026-04-05T10:30:00+08:00",
  "data_dir": "C:\\Users\\...\\data",
  "webapp_url": "http://127.0.0.1:0/app/"
}
```

**发现流程：**

```
桥接插件启动
    │
    ▼
扫描约定目录查找元数据文件
    │
    ├── 文件存在 + Token 可用 → 连接 HTTP API
    │   └── 轮询直到 port > 0（独立软件分配后写入）
    │
    └── 文件不存在 → 提示用户启动独立软件
```

### 10.3 移除的自动认证逻辑

| 移除内容 | 说明 |
|---------|------|
| `DutyPythonIpcService.cs` — Python 进程启动 | 独立软件已包含 |
| Bootstrap stdout 解析 (`__DUTY_SERVER_PORT__` 等) | 改用文件传递 |
| Job Object / `PythonProcessTracker.cs` | 不管理子进程 |
| 固定端口冲突回退逻辑 | 独立软件处理 |
| `DutyAccessTokenModes` 动态/静态 Token | 仅读取元数据文件 |
| `DutyPluginLifecycle.cs` 生命周期管理 | 独立软件管理 |

### 10.4 新建插件目录结构

```
Duty-Agent-ClassIsland-Bridge/
├── manifest.yml
├── DutyAgentBridge.csproj
├── Plugin.cs
│
├── Services/
│   ├── IpcBridgeService.cs          # IPC 通信核心
│   ├── IpcBridgeModels.cs          # 通信数据模型
│   └── IpcBridgeDiagnostics.cs     # 诊断日志
│
├── Controls/
│   ├── DutyComponent.axaml
│   ├── DutyComponent.axaml.cs
│   ├── DutyComponentSettingsControl.axaml
│   └── DutyComponentSettingsControl.axaml.cs
│
├── Views/
│   ├── SettingsPage.axaml
│   └── SettingsPage.axaml.cs
│
├── Models/
│   ├── NotificationModels.cs
│   └── SettingsModels.cs
│
└── Assets/
```

### 10.5 核心服务设计

**IpcBridgeService — IPC 通信：**

```csharp
public interface IIpcBridgeService : IDisposable
{
    IpcBridgeState State { get; }
    string? LastError { get; }
    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync();
    event EventHandler<IpcBridgeState>? StateChanged;
    Task<CoreRunResult> RunScheduleAsync(string instruction, Action<CoreRunProgress>? progress = null, CancellationToken ct = default);
    Task<CoreRunResult> RollbackAsync(CancellationToken ct = default);
    Task CancelAsync(CancellationToken ct = default);
    Task<DutyBackendSnapshot> GetSnapshotAsync(CancellationToken ct = default);
    Task<DutyBackendConfig> GetConfigAsync(CancellationToken ct = default);
    Task<DutyBackendConfig> UpdateConfigAsync(DutyBackendConfigPatch patch, CancellationToken ct = default);
    Task SaveScheduleEntryAsync(DutyScheduleEntrySaveRequest request, CancellationToken ct = default);
}
```

### 10.6 保留的 ClassIsland 继承点

| 功能 | 实现 |
|------|------|
| 通知系统 | `DutyNotificationProvider : NotificationProviderBase` |
| 桌面组件 | `DutyComponent : ComponentBase<DutyComponentSettings>` |
| 设置页 | `SettingsPage : SettingsPageBase` |
| 自动化 Action | `RunDutyScheduleAction : ActionBase<RunDutyScheduleActionSettings>` |
| 自动化 Rule | `DutyAssignedStudentRule : RuleHandlerBase` |

### 10.7 不包含的功能

由于独立软件已有完整功能，桥接插件**不包含**：

- WebView2 设置页
- 名册管理 UI
- 排班预览/编辑 UI
- AI 工具配置 UI
- 自动运行配置 UI
- Python 进程管理

### 10.8 实施步骤

| 步骤 | 任务 |
|------|------|
| 1 | 创建 `Duty-Agent-ClassIsland-Bridge/` 项目结构 |
| 2 | 实现 `IpcBridgeService`（HTTP/WebSocket 通信） |
| 3 | 实现 `DutyNotificationProvider`（通知继承） |
| 4 | 实现 `DutyComponent`（桌面组件继承） |
| 5 | 实现 `SettingsPage`（状态显示+入口链接） |
| 6 | 实现 `RunDutyScheduleAction`（自动化动作） |
| 7 | 实现 `DutyAssignedStudentRule`（自动化规则） |
| 8 | 可选：独立软件添加元数据文件写入支持 |