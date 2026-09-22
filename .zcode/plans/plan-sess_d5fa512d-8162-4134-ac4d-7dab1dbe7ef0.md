# 全量修复计划：突发情况处理（请假/缺席/临时调整/大扫除）

## 0. 背景与目标

审计确认的 5 个缺陷，本计划全部闭环：

| # | 缺陷 | 现状证据 |
|---|---|---|
| F1 | 当次缺席只在 6-Agent 链有硬校验；标准/工具循环/Orchestrator 链靠模型自觉，Python 不拦 | `apply_single_pass_completion` 无缺席检查；V2 `[state]` 无 absence 字段 |
| F2 | `next_run_note` 无用户写入入口，且 multi_agent/orchestrator 结算时被机器摘要覆盖（tool_loop 又从不清理——不一致）；无日期感知，自动排班会误消费"周三大扫除" | `settlement.py:37`、`orchestrator/executor.py:747`、`single_pass_executor.py:90` |
| F3 | `area_per_day_counts` 被接收后直接 `del`（代码自注 "not yet enforced"），缺编/超编无人发现；"某天加人"无结构化通道 | `postprocess.py:176-178`；`_build_required_slots` 只读 config 静态人数 |
| F4 | 缺席无日期维度：Agent2 `absent_ids` 平铺列表只作用本轮，"请假三天"无法跨轮持久 | `multi_agent/prompts.py:75` |
| F5 | 人手不足时报错是 `slot count mismatch on 2026-03-16/教室` 之类机器语言 | `multi_agent/validators.py:339` |

长期屏蔽（roster `active=0`）与债务账本现有机制**不动**。

## 1. 已拍板决策（用户确认）

- **D-A 人数校验**：缺编自动补齐 + 超编裁剪。补人顺序：债务未清者优先 → 轮转序（自 `last_pointer`）；裁剪顺序：先裁无债务、无 must-run 者，按轮转逆序。所有修正写入当日 note + 结果 warnings。
- **D-B 入口范围**：API + CLI + MCP（本轮不做 UI）。
- **D-C 请假默认跨度**：自然语言未说明时长 → 默认当天 1 天；"三天/一周/到周五"等解析为区间；更长用 CLI/API 显式指定。

## 2. 新增状态结构（state.json，全部向后兼容）

```
absences:      [{"id": 1005, "from": "2026-09-13", "to": "2026-09-15"}]
user_notes:    [{"text": "周三大扫除，教室加2人", "until": "2026-09-17", "created_at": "..."}]
day_overrides: {"2026-09-16": {"教室": 4}}
```

- `_read_state_json`（state_ops.py:643）补三字段归一化默认值（缺省 `[]`/`[]`/`{}`），旧 state 文件零迁移。
- 新增 `prune_operational_state(state, today) -> state`：清理 `to < today` 的缺席、`until < today` 的备注、日期已过的 override。**所有结算写入点统一调用**——顺带修复 tool_loop 从不清理备注的不一致（`tool_loop/executor.py:_write_state` 现在原样保留）。
- `next_run_note` 保留为机器摘要语义不变（现有覆盖行为不动），用户备注走新 `user_notes`，互不覆盖。

## 3. 新增纯函数模块 `Assets_Duty/absence_ops.py`

- `extract_absentees(raw_instruction, name_to_id, default_days=1) -> list[dict]`：按标点（，。；、！？）分句；句内含缺席关键词（请假/缺席/生病/不舒服/不在/离开/休息）且含名单姓名 → 记缺席（防"张三请假，李四补上"误伤李四）。解析"三天/一周/到 MM-DD"时长，无时长按 D-C 取 1 天。**必须在 `anonymize_instruction` 之前对原始指令运行**（4 处 bootstrap 点均先取原始指令）。
- `absences_for_window(absences, window_start, window_end) -> set[int]`：区间重叠判定。
- `compose_run_notes(state, today) -> str`：机器摘要 + 未过期 user_notes 合并；替换 4 处读取点（`single_pass_executor.py:90`、`orchestrator/executor.py:481`、`multi_agent/executor.py:90`、`tool_loop/executor.py:258`）。
- `fill_shortfall(...)` / `trim_excess(...)`：确定性缺编补齐/超编裁剪（实现 D-A 顺序），候选池 = 在岗 − 缺席 − 当日已排。

## 4. Wave A：协议与状态核心

1. **state_ops.py**：三字段归一化；`prune_operational_state`；`absence_ops.py` 落位。
2. **llm_transport.py `_parse_state_section`**：新增可选行 `absent = 1001 1002` → `state_delta["absent_ids"]`（向后兼容，CSV fallback 不受影响；语法与 `[state]` 现有约束一致，无 `:`）。
3. **prompt_config.py + build_prompt.py**：`param_inactive` 扩展为合并"roster 不活跃 ∪ 窗口内持久缺席"；输出协议说明补 `absent` 行文档；`build_prompt_messages` 增加 `absent_ids`/`day_overrides` 入参，`required_areas` 存在 override 时按日期列出。
4. **各链 bootstrap**（single_pass `_freeze`/build、tool_loop、orchestrator `_orchestrator_bootstrap`、multi_agent `_freeze_snapshot`）：anonymize 前调用 `extract_absentees`，结果**请求时即持久化**到 `state.absences`（plan-prompt/plan-ingest 分离流程下，缺席在 build 半段落盘、apply 半段读取——与现有 stateless resume 设计一致），并注入 prompt。

## 5. Wave B：四条执行链

5. **single_pass（`apply_single_pass_completion`）**：
   - 缺席校验：排班结果含缺席者（持久缺席 ∪ 模型 `[state]` absent声明）→ 摘除该 ID，按 D-A 补位，记 warning + 当日 note。
   - 人数校验落地：`normalize_multi_area_schedule_ids` 用 required = config `area_per_day_counts` + `day_overrides`（替换 `del` 行）；缺编 `fill_shortfall`、超编 `trim_excess`；修正进 warnings。
6. **tool_loop（ini_handler.py + executor.py）**：`ExecutionCtx` 增加 `absent_ids`；`_accept_slot` 增加 `ABSENT` 拒绝码（镜像现有 INACTIVE 分支 ini_handler.py:610）；`_build_required_slots` 应用 `day_overrides`（工具循环的 `[remaining]` 机制天然驱动模型填满大扫除加派人手）；`_write_state` 调 prune。
7. **orchestrator**：与 tool_loop 共用 ini_handler，自动获得缺席校验与 override 槽位；`_orchestrator_bootstrap` 注入缺席名单到 shared_state（poll 示例已有"04-03 A区需要 3 人（班级活动）"式样）；`_finalize` 调 prune。
8. **multi_agent**：Agent2 payload 增加 `suggested_absent_ids`（Python 提取，作 hint）；`merge_barrier1` 合并持久缺席（按日期窗口过滤）；Agent6 调用前新增容量预检：`max(单日槽位合计) > 可用人数(active − 窗口内缺席)` → 人性化 ValueError（中文，含"需要 N 人 / 可用 M 人 / 建议：减少每日人数、缩短日期或恢复名单"）；`fallback_fill_schedule` 失败时包装上下文（池大小 vs 槽位）；settlement 调 prune。

## 6. Wave C：入口（D-B 范围）

9. **models/schemas.py**：`DutyAbsenceRequest`（action: add/clear; person id/name; from/to 或 days）、`DutyRunNoteRequest`（action: add/clear; text; until）、`DutyDayOverrideRequest`（action: set/clear; date; area; count）。
10. **routers/duty.py**：`POST /absences`、`POST /run-notes`、`POST /day-overrides`（Bearer 保护同现有端点；姓名入参内部经 `load_roster` 转 ID）。
11. **application/command_service.py**：三个对应方法，统一走 `update_state` 文件锁。
12. **cli.py**：新子命令 `absence`（`--person/--days/--until/--clear`）、`note`（`--text/--until/--clear`，支持 `--note-file`）、`override`（`--date/--area/--count/--clear`）；`describe` 目录同步。
13. **mcp_server.py**：`manage_absences`、`manage_run_notes`、`manage_day_overrides` 三个工具（FastMCP `@mcp.tool` 模式）。

## 7. Wave D：测试与文档

14. **新增 `Assets_Duty/test_repair_wave2.py`**（沿用 test_repair_wave1 的 tempfile 数据目录约定，绝不触碰 %LOCALAPPDATA%）：缺席提取（分句/时长/误报防护/匿名化前时序）；四链缺席排除各一例；V2 `absent` 行解析；`compose_run_notes`/prune 过期清理；override → required slots（tool_loop `[remaining]` 数量）；normalize 缺编补齐/超编裁剪（D-A 顺序断言）；multi_agent 容量预检报错文案；旧 state 兼容（无新字段文件可读）。
15. **文档**：README「当前能力」补三条；AGENTS.md V2 语法补 `absent = ...` 行；`cli describe` 输出同步。

## 8. 验证与交付

- 每 Wave 完成即跑新增测试 + 相关回归（`test_tool_loop`、`test_core`、`test_offline_scheduler`、`test_schedule_entry_api`、`test_execution_profiles_and_multi_agent`、`test_llm_transport`）。
- 全部完成后跑一次全量测试套件。
- 沿用 REPAIR_PLAN 惯例：不做 git commit，提交时机由你决定。

## 9. 明确不做（本轮范围外）

- UI「临时调整」卡片（duty-agent-ui）——待后端入口稳定后另行安排。
- 缺席自动记欠账（缺席是否算债属产品语义，现有账本仅由手动调班触发，保持现状）。
- multi_agent "同一人可跨天复用"的池约束放宽（现状语义保留，仅加容量预检报错）。