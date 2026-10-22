# 校验策略（validation policy）

后端对 LLM 排班结果的校验，产品上是**三档**，由一个策略表统一分派。

设计前提：这套校验是 2026 年 2 月按"模型会乱写"的假设堆起来的。当前云端模型基本不再犯那些错
（结构化输出、日期顺序、区域人数都稳），只有 0.8B 级本地小模型还需要兜底。继续无条件自动修正的
代价是：**一个能干模型的排班被代码静默改写（补人、裁人），而用户无从关闭**。三档就是把"信任模型"
变成可选项，而不是唯一行为。

## 档位

| 档位 | 语义 | 适用 |
|---|---|---|
| `relaxed` 宽松模式（**默认**） | 尊重模型输出。人数缺口、日期覆盖、区域结构等问题**只警告，不自动修改排班** | 云端模型 |
| `strict` 严格模式 | 完整检查区域人数、目标日期与业务规则，任一不满足即**拒绝提交**并列出具体违例 | 对结果有硬性契约的场景 |
| `repair` 实验性智能修复 | 尝试自动补齐、裁剪、调整，**每一次修改都以警告形式列出** | 0.8B 级本地小模型 |
| `auto` 自动 | 按模型画像解析：`campus_small` / `edge` → `repair`，其余 → `relaxed` | 不想理解三档的人 |

配置键 `config.json` 的 `validation_mode`（`PATCH /api/v1/config`，热生效，无需重启后端）。
运行时投影额外给出 `effective_validation_mode`——`auto` 展开后的实际档位，设置页据此展示"当前生效"。

## 四种 action

`Assets_Duty/validation_policy.py` 的 `CHECK_ACTIONS` 是唯一真源，每个检查按档位分派到四种动作之一：

- `reject` —— 抛错，什么都不提交。
- `warn` —— **原样保留模型输出**，只追加一条警告。
- `repair` —— 确定性修复，并且必须产出描述该修改的警告。
- `enforce` —— 与档位无关；防的是数据损坏，不是模型犯错。

执行器通过 `policy.action("<check>")` 查表，不自己分支档位。新增一个档位或重新归类一个检查，
都是表里的一行改动。

## 与档位无关的检查

以下**任何档位都不关闭**，因为它们保护的是数据一致性而不是模型的发挥空间：

| 类别 | 检查 |
|---|---|
| 协议解码 | INI 段结构、MM-DD 合法性、token 语法、`[state]` 键白名单 |
| 无歧义 | 日期严格递增、重复日期、同一区域重复 ID |
| 完成度 | 空排班池（`empty_pool`）——"没有可提交的东西"不是模型风格 |
| 账本 | 账本幂等、模型声明的债务/积分按花名册过滤 |
| 历史 | `apply_mode` 历史保留（默认 `append`） |
| 回滚 | 名单代际指纹校验 |
| 写入 | 原子提交 + 文件锁 |

## 各档位的实际行为

### relaxed（默认）

| 情形 | 行为 |
|---|---|
| 区域人数不足 / 超员 | 按模型原样提交 + 警告（`…（宽松模式未修改）`），**不写"自动修正"备注** |
| 窗口缺日 / 越界日期 | 同上，只警告 |
| 未配置区域的幻影区域 | 只警告 |
| must_run（"必须让 X 上"）未满足 | 只警告——模型对"谁值日"的判断优先于一条它选择不遵守的提示 |
| 人员不在花名册 | **拒绝** |
| 已停用人员 | **拒绝** |
| 当天请假/缺席 | **拒绝** |
| 同一区域同一人重复 | **拒绝** |

后四条是"与本地已知事实冲突"：花名册和请假记录是本地权威数据，安排一个已毕业或正在请假的人
不是"模型的风格问题"，静默排进去比拒绝更危险。

### strict

relaxed 里所有"只警告"的项升级为拒绝：区域人数必须等于配置、必须覆盖全部目标日期、不得越界、
must_run 必须满足。拒绝时列出具体违例（日期/区域/期望值/实际值）。

multi_agent 的 agent6 **重试边界不查策略**，固定按 strict 走：那里要回答的是"模型的输出还不可用，
再要一次"，而这个答案在任何档位下都一样。策略决定的是"活到结算阶段的偏差怎么处理"。

### repair

历史行为，现在经由策略表到达：剔除停用/缺席、超额裁剪、缺额按轮转序补齐，每条都进
`adjustment_warnings` 并在 UI 与通知里展示。

## 有修就有修，没修别声称有修

`repair` 动作必须名实相符。窗口类检查就是一个例子：multi_agent 有兜底（`fallback_fill_schedule`
会产出模板里的每一天），所以 `window_gap` 在 repair 档是"移交给代码层"；single_pass 没有补日期的能力，
同一个检查在那里退化为警告。因此引入了两个谓语：

- `policy.is_fatal(check)` —— 只有 `reject` 为真。
- `policy.delegates_to_code(check)` —— `reject` 或 `repair` 为真；"模型的输出不能原样接受，代码层接手"。

`out_of_window` / `phantom_area` / `template_mismatch` 目前只报告不修复，表里就写 `warn`，
不假装能修。

## 跨区域同日复用

**允许**，任何档位都不禁止，也不静默剔除。产品规则只禁止"同一区域、同一天重复 ID"。
不得在没有产品规则变更的情况下增加全日跨区域唯一性限制。

## 排障

- **排班人数比配置少** → 当前是宽松模式，模型少排了，后端只警告。想看自动补齐就切 `repair`，
  想让它直接失败就切 `strict`。
- **"安排了已停用人员"被拒** → 这是设计如此，任何档位都拒。要么在名单管理里恢复该成员，要么改排班。
- **选了自动但行为像宽松** → 当前方案画像不是 `campus_small` / `edge`；设置页"当前生效"会显示解析结果。
- **想确认后端实际用哪档** → `GET /api/v1/config` 的 `effective_validation_mode`。

## 相关代码

| 文件 | 职责 |
|---|---|
| `Assets_Duty/validation_policy.py` | 策略表、档位归一化、`auto` 解析、`ValidationPolicy` / `ValidationReport`、UI 文案 |
| `Assets_Duty/single_pass_executor.py` | `_enforce_absence_and_counts` / `_audit_schedule_window` 按表分派 |
| `Assets_Duty/multi_agent/validators.py` | `validate_final_schedule` 的策略相关项（must_run、窗口覆盖） |
| `Assets_Duty/multi_agent/settlement.py` | 从 `snapshot.config` 建策略，警告汇入 `barrier2["warnings"]` |
| `Assets_Duty/runtime.py` | `collect_result_warnings` 把两种形态的警告送进通知与 UI |
| `Assets_Duty/test_validation_policy.py` | 策略矩阵 + 配置链路 + 端到端档位行为 |
