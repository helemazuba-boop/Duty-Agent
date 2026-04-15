# Multi-Agent Orchestrator 架构规格书 (SPEC)

> 起草时间：2026-04-05
> 状态：**草稿**，待评审

---

## 1. 背景与目标

### 1.1 为什么重构

现有 6-Agent 架构（Agent1~6 + 2 Barrier）按专业分工（债务/规则/填充），导致：

| 问题 | 说明 |
|------|------|
| **职责边界模糊** | Agent1/2/3 各自输出 JSON，Barrier 合并时高度耦合 |
| **无自我修正** | Agent6 单次生成，失败才走 `fallback_fill_schedule` |
| **INI 解析器是孤岛** | ST 模式有 hints 循环，Multi-Agent 模式不经过 `ini_handler.py` |
| **Token 浪费** | 6 个 Agent 各有 system prompt，大量重复状态描述 |

### 1.2 重构目标

1. **按轮次+时间分工**：Agent 以「时间窗口」划分，而非按专业分工
2. **Orchestrator 单一调度**：Orchestrator 承担任务理解、时间窗口切分、结果汇总职责
3. **TimeWindow Agent 并行**：多个 Agent 同时处理不同时间窗口，互不干扰
4. **统一仲裁层**：所有输出最终经过 `ini_handler.py` 校验和持久化
5. **Hints 修正循环**：Orchestrator 收集各 Agent 结果后，若有冲突则修正重试

---

## 2. 核心概念

### 2.1 轮次（Round）

轮次 = 「轮到」。一个轮次 = **所有学生各值一次班**。

例如：20 名学生，A/B 两区，每天各 2 人。

- 轮次 1：所有 20 名学生各值一次 → 总共 10 个班次（A区 5 天 × 2 人，B区 5 天 × 2 人）
- 轮次完成 = `[remaining]` 为空（或 Orchestrator 输出 `@finalize`）

### 2.2 时间窗口（TimeWindow）

Orchestrator 将一个轮次**动态切分**为多个时间窗口：

```
轮次 1（假设 20 人，10 天）
  ├─ 时间窗口 1：04-01 ~ 04-03  → TimeWindow Agent A
  ├─ 时间窗口 2：04-04 ~ 04-06  → TimeWindow Agent B
  └─ 时间窗口 3：04-07 ~ 04-10  → TimeWindow Agent C
```

切分策略由 Orchestrator 动态决定，基于：
- 学生总数（多 → 窗口多；少 → 窗口少）
- 债务分布（债务集中的日期单独成窗口）
- 班次密度（每天班次均匀时不拆分）

### 2.3 两种 Agent 角色

| 角色 | 职责 | 数量 |
|------|------|------|
| **Orchestrator** | 理解任务、切分窗口、汇总结果、hints 修正 | 1 |
| **TimeWindow Agent** | 填充指定时间窗口内的排班 | N（并行） |

---

## 3. 架构概览

```
┌──────────────────────────────────────────────────────────┐
│  Orchestrator (LLM Agent, 单一)                         │
│                                                          │
│  输入: instruction + FrozenSnapshot                      │
│  决策: 动态切分 round → N 个时间窗口                     │
│  分派: 并行调度 N 个 TimeWindow Agent                    │
│  汇总: 收集所有 INI 片段 → 送入 INI 解析器               │
│  修正: 有 hints → 分析 → 重试对应窗口                    │
└────────────────────┬───────────────────────────────────┘
                     │
          ┌──────────┴──────────┬───────────────┐
          │                      │               │
          ▼                      ▼               ▼
┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐
│ TimeWindow      │  │ TimeWindow      │  │ TimeWindow      │
│ Agent A         │  │ Agent B         │  │ Agent C         │
│ 04-01 ~ 04-03   │  │ 04-04 ~ 04-06   │  │ 04-07 ~ 04-10   │
│ INI 片段 A      │  │ INI 片段 B      │  │ INI 片段 C      │
└────────┬────────┘  └────────┬────────┘  └────────┬────────┘
         │                      │               │
         └──────────────────────┴───────────────┘
                                 │
                    Orchestrator 汇总所有 INI 片段
                                 │
                                 ▼
                  ┌──────────────────────────────┐
                  │  ini_handler.py（仲裁层）    │
                  │                              │
                  │  Phase 1: 解析批次 + @cmds  │
                  │  Phase 2: 验证 + 状态计算    │
                  │                              │
                  │  → finalized → state.json   │
                  │  → hints → Orchestrator 修正  │
                  └──────────────────────────────┘
```

---

## 4. Orchestrator Agent

### 4.1 职责

1. **理解任务**：解析用户的自然语言指令
2. **读取上下文**：从 `FrozenSnapshot` 获取债务/积分/规则/学生池等
3. **切分时间窗口**：将一个完整轮次划分为 N 个时间窗口（动态决定）
4. **分派 Agent**：并行调度 N 个 TimeWindow Agent，各持有一个 INI 上下文片段
5. **汇总 INI**：收集各 Agent 的 INI 片段，拼接为完整 INI 文本
6. **送入解析器**：将完整 INI 送 `ini_handler.parse_and_apply`
7. **解读 hints**：若解析器返回 hints，分析并决定：
   - 重试特定时间窗口（发 hints 给对应 Agent）
   - Orchestrator 直接修正（简单冲突）
   - 强制 finalize（无法修正）
8. **持久化**：确认 finalized 后通知 settlement

### 4.2 OrchestratorContext

```python
@dataclass
class OrchestratorContext:
    trace_id: str
    request_time: datetime
    start_date: date
    round: int                    # 当前轮次（=1，完整轮次）
    pointer: int                  # 轮询指针位置
    all_ids: List[int]           # 所有学生 ID
    active_ids: List[int]        # 可用学生 ID
    inactive_ids: List[int]      # 不可用学生 ID
    debt_list: List[int]         # 带权重债务列表
    credit_list: List[int]       # 带权重积分列表
    duty_rule: str               # 排班规则描述
    id_to_name: Dict[int, str]   # ID → 姓名
    id_to_area: Dict[int, str]   # ID → 归属区域
    all_areas: List[str]         # 所有区域（每个区域都需填）
    area_per_day: Dict[str, int] # 区域 → 每天人数
    hints_on: bool
    max_rounds: int = 15
```

### 4.3 时间窗口切分策略

```python
WEEK_THRESHOLD_DAYS = 7  # 总安排量 < 7 天 → 不分派 TimeWindow Agent

def should_use_timewindow_agents(context: OrchestratorContext) -> bool:
    total_slots = sum(
        len(context.area_per_day.get(area, 0)) * len(context.total_dates)
        for area in context.all_areas
    )
    return total_slots >= WEEK_THRESHOLD_DAYS

def decompose_round(context: OrchestratorContext) -> List[TimeWindow]:
    """
    动态切分策略：
    - total_slots < 7：不分派 Agent，Orchestrator 自己做
    - 7 ~ 21 slots：按债务分布切分（债务集中的日期优先独立窗口）
    - > 21 slots：按连续日期段切分，每段 3~5 天
    """
```

切分结果：

```python
@dataclass
class TimeWindow:
    index: int                    # 窗口编号（0-based）
    name: str                     # 窗口名称，如 "week1", "phase2"
    start_date: date
    end_date: date
    dates: List[date]            # 该窗口包含的所有日期
    area_slots: List[ScheduleSlot]  # 该窗口的所有 (date, area) 待填充 slot
    debt_ids: List[int]          # 该窗口优先要填的债务 ID
    available_ids: List[int]     # 该窗口可用的学生 ID
```

### 4.4 Orchestrator → TimeWindow Agent 的分派

每个 TimeWindow Agent 收到独立的 INI 上下文片段：

```ini
; === TimeWindow: phase1 (04-01 ~ 04-03) ===
; 你的时间范围：04-01, 04-02, 04-03
; 可用学生：1001, 1002, 1003, 1004, 1005
; 优先债务：1004*2, 1002
; 区域：A区（每天2人）, B区（每天2人）

[window]
name = phase1
dates = 2026-04-01, 2026-04-02, 2026-04-03
areas = A, B

[remaining]
2026-04-01 = A:?
2026-04-01 = B:?
2026-04-02 = A:?
2026-04-02 = B:?
2026-04-03 = A:?
2026-04-03 = B:?

; 输出格式：
; [[#phase1]]
; 2026-04-01 = A:1001 1002 | B:1003 1004
; ...
```

### 4.5 汇总与 hints 修正

```python
def process_round(context: OrchestratorContext) -> ProcessResult:
    windows = decompose_round(context)
    ini_fragments = []

    # 并行分派所有 TimeWindow Agent
    with ThreadPoolExecutor(max_workers=len(windows)) as executor:
        futures = {
            executor.submit(call_timewindow_agent, w, context): w
            for w in windows
        }
        for future in as_completed(futures):
            ini_fragments.append(future.result())

    # 汇总
    combined_ini = concatenate_ini_fragments(ini_fragments)

    # 送入 INI 解析器
    result = ini_handler.parse_and_apply(
        combined_ini, context, flags, previous_units=[]
    )

    if result.finalized:
        return ProcessResult(finalized=True, state=result.state_delta)

    # 有 hints → 分类处理
    for hint in result.hints:
        if hint.affects_window == window_index:
            # 重试对应窗口
            retry_agent(windows[hint.affects_window], hint.message)
        else:
            # Orchestrator 直接处理
            orchestrator_fix(hint)

    return ProcessResult(finalized=False, hints=result.hints)
```

---

## 5. TimeWindow Agent

### 5.1 职责

- 接收 Orchestrator 分派的固定时间窗口
- 根据债务/积分/规则，填充该窗口内所有 `(date, area)` slot
- 输出 INI 片段（不输出 `@finalize`，由 Orchestrator 决定）
- 不与外部交互（无 hints 循环，简单单次执行）

### 5.2 System Prompt 模板

```markdown
你是排班填充 Agent，只负责你的专属时间窗口。

## 时间窗口
- 窗口名称：{window_name}
- 日期范围：{start_date} ~ {end_date}
- 包含日期：{dates}

## 可用学生
{available_ids}

## 优先债务（优先安排这些学生）
{debt_ids}

## 区域信息
{area_info}

## 排班规则
{duty_rule}

## 输出要求

1. 输出 INI 格式的排班片段，使用 [[#{window_name}]] 标注
2. 为 [remaining] 中的每个 slot 分配学生
3. 不要重复安排同一人同一天
4. 遵守排班规则
5. 不输出 @finalize（由 Orchestrator 决定）

## 格式示例

[[#{window_name}]]
{start_date} = A:ID1 ID2 | B:ID3 ID4
...
```

### 5.3 执行策略

TimeWindow Agent 使用**极简配置**：

```python
TIMEWINDOW_AGENT_CONFIG = {
    "model": "qwen3.5:9b",
    "temperature": 0.1,        # 低温度，保证确定性
    "max_tokens": 2048,
    "hints_on": False,         # TimeWindow Agent 无 hints 循环
    "timeout": 60,             # 超时保护
    "max_retries": 2,         # 超时重试次数
    # ⚠️ 不暴露任何工具，Agent 纯文本输出 INI 片段
}
```

### 5.4 超时处理

```python
def call_timewindow_agent(window: TimeWindow, context: OrchestratorContext) -> str:
    for attempt in range(TIMEWINDOW_AGENT_CONFIG["max_retries"] + 1):
        try:
            return llm_call(window_prompt, config=TIMEWINDOW_AGENT_CONFIG, timeout=60)
        except TimeoutError:
            if attempt < TIMEWINDOW_AGENT_CONFIG["max_retries"]:
                continue  # 重试
            else:
                # 超时 → 该时间窗口留空，由 Orchestrator 自己补
                return f"; [[#{window.name}]]\n; TIMEOUT: agent 超时，留空待 Orchestrator 补全"
```

---

## 6. INI 解析器（仲裁层）

### 6.1 统一接口

`ini_handler.py` 是所有模式（ST / Orchestrator）的唯一仲裁层。

```python
def parse_and_apply(
    ini_text: str,
    context: Union[PollingContext, OrchestratorContext],
    flags: PollingFlags,
    previous_units: List[ScheduleUnit],
) -> ParseResult:
    """
    Returns:
        ParseResult(
            finalized: bool,
            hints: List[Hint],
            response_ini: str,          # 权威模板（含 Python 计算的状态）
            updated_units: List[ScheduleUnit],
            state_delta: Dict,          # debt/credit/pointer 变化
            applied_schedule: List[Slot],  # 已落表的 slot 列表
        )
    """
```

### 6.2 hints 分类

| 级别 | 含义 | Orchestrator 响应 |
|------|------|----------------|
| `CONFLICT` | 硬冲突（如：同一人重复排班） | 重试对应时间窗口 |
| `WARNING` | 软警告（如：某天缺少人员） | Orchestrator 补全 |
| `INFO` | 信息（如：债务已清零） | 忽略，继续 |

### 6.3 hints 格式（追加在 INI 注释中）

```ini
; hints:
; CONFLICT: 1002 在 2026-04-01 已排班，不能重复安排到 2026-04-01 B区
; CONFLICT: 2026-04-02 A区超员（需2人，实际3人）
; WARNING: 2026-04-03 B区缺少1人，当前 pool=[1009] 不够
; INFO: 1004 债务已清零，可正常安排

[state]
round = 1
pointer = 7
debt =
credit = 1001*1
```

---

## 7. 完整执行流程

```
第0步：Orchestrator 初始化
  ├─ 读取 FrozenSnapshot
  ├─ 计算 total_slots
  └─ total_slots < 7？
      ├─ 是 → Orchestrator 自己填充整个轮次（不分派 Agent）
      └─ 否 → decompose_round → 切分时间窗口 → 并行分派

第1步：TimeWindow Agent 执行（并行，timeout 60s × 2 次）
  ├─ Agent A → INI 片段 A（超时则留空）
  ├─ Agent B → INI 片段 B（超时则留空）
  └─ Agent C → INI 片段 C（超时则留空）

第2步：Orchestrator 汇总
  └─ 拼接 INI 片段 → 完整 INI 文本

第3步：INI 解析器校验
  ├─ Phase 1：解析批次 + 执行 @commands
  └─ Phase 2：验证 schedule + 计算状态

第4步：判定
  ├─ finalized=True → 持久化 → 结束
  └─ finalized=False → 第5步

第5步：Hints 修正（最多 max_rounds 轮）
  ├─ Orchestrator 分析 hints
  ├─ 分派重试到对应 TimeWindow Agent
  ├─ 重新汇总 → 重新送解析器
  └─ 达到 max_rounds → 强制 finalize

第6步：Settlement
  └─ 调用 settlement.py 持久化 state.json
```

---

## 8. 文件结构

```
Assets_Duty/
├── orchestrator/
│   ├── __init__.py
│   ├── context.py              # OrchestratorContext 数据类
│   ├── decomposer.py           # 时间窗口动态切分逻辑
│   ├── prompt.py                # Orchestrator + TimeWindow Agent prompts
│   ├── executor.py             # Orchestrator 主循环（入口）
│   ├── aggregator.py           # INI 片段汇总逻辑
│   └── hints_handler.py        # Hints 分类与分发
│
├── tool_loop/
│   └── ini_handler.py          # （已有）统一仲裁层
│
├── multi_agent/
│   ├── executor.py             # （旧版 6-Agent，待废弃）
│   ├── settlement.py           # （复用）持久化逻辑
│   └── validators.py           # （复用）fallback 逻辑
```

---

## 9. ST 模式 vs Orchestrator 模式

| 维度 | ST 模式 | Orchestrator 模式 |
|------|---------|------------------|
| 调度器 | Python 代码（轮询循环） | LLM Agent |
| 时间窗口 | 无（整体一次性） | 动态切分为 N 个并行窗口 |
| Specialist | 无 | TimeWindow Agent（按需并行） |
| Hints 修正 | Python → LLM 两跳 | Orchestrator 直接分析，单跳 |
| 适用场景 | 简单指令 | 复杂/多日期/需并行的场景 |

**两模式共享 `ini_handler.py` 仲裁层，最终都落 `state.json`**。

---

## 10. 实施计划

### 阶段 A：核心模块（新建）

| 步骤 | 任务 | 文件 |
|------|------|------|
| A1 | 定义 `OrchestratorContext`、`TimeWindow` 数据类 | `orchestrator/context.py` |
| A2 | 实现时间窗口动态切分 | `orchestrator/decomposer.py` |
| A3 | 实现 Orchestrator prompt 模板 | `orchestrator/prompt.py` |
| A4 | 实现 TimeWindow Agent prompt | `orchestrator/prompt.py` |
| A5 | 实现 INI 片段汇总 | `orchestrator/aggregator.py` |
| A6 | 实现 Hints 处理与分发 | `orchestrator/hints_handler.py` |
| A7 | 实现 Orchestrator 主循环 | `orchestrator/executor.py` |
| A8 | 更新 `engine.py` 入口选择逻辑 | `engine.py` |

### 阶段 B：集成测试

| 步骤 | 任务 |
|------|------|
| B1 | 20 人/10 天场景，Orchestrator 正确切分为 3 个时间窗口 |
| B2 | TimeWindow Agent 并行执行，各自输出 INI 片段 |
| B3 | Orchestrator 汇总 + INI 解析器校验 |
| B4 | Hints 循环（2~3 轮修正）|
| B5 | `@finalize` → `state.json` 持久化 |

### 阶段 C：收尾

| 步骤 | 任务 |
|------|------|
| C1 | 标记旧 `multi_agent/executor.py` 为废弃 |
| C2 | 更新 `workspace.md` |
| C3 | 文档 |

---

## 11. 待确认问题

- [x] `avoid_consecutive_days` 等规则在 INI 解析器中已完整覆盖
- [x] TimeWindow Agent 超时后：重试（60s × 2 次）
- [x] `max_rounds=15` 合理，沿用
- [x] 旧 6-Agent 架构：完成后废弃
- [x] Orchestrator 放弃 Agents 阈值：**总安排量 < 1 周（< 7 天）**时，Orchestrator 直接自己做，不分派 TimeWindow Agent

---

## 12. 附录：与旧架构对比

| 维度 | 旧 6-Agent | 新 Orchestrator |
|------|-----------|----------------|
| 分工方式 | 专业分工（债务/规则/填充） | 时间分工（按时间窗口） |
| Agent 数量 | 6 | 1 + N（并行）|
| 修正机制 | 无 | hints 循环 |
| 仲裁层 | 分散 | 统一 `ini_handler.py` |
| 并行能力 | Barrier 同步，无真正并行 | N 个 TimeWindow Agent 真并行 |
| Token 效率 | 低（6 份 system prompt） | 高（Orchestrator 1 份 + TimeWindow N 份精简版）|
| 可解释性 | 中（各 Agent trace） | 高（INI 文本 + hints 注释）|
