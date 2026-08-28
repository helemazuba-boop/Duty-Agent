# HANDOFF — 长期运行可靠性全量修复（2026-08-23）

> 交接快照。分支 `independent-agent`，基于 commit `14b8307`。
> 工作区有 **8 个未提交的已修改文件**，全部是本次修复的产物。**不要 reset / stash / clean / checkout 覆盖它们。**

## 1. 当前目标

项目近期落地了"持久化运行"基础（auto-run worker、值日提醒、通知总线、bridge 心跳），但排查发现若干会导致长期运行失效的问题。本任务：**全量修复**这些问题并验证构建/测试。

## 2. 已经确认的事实（排查结论）

1. **引擎崩溃后永不自愈**：`process.Exited` 只置 `EngineState.Faulted`；`EnsureReadyAsync` 遇 Faulted 直接 throw。auto-run/提醒/MCP 全部活在 Python 进程内 → Python 一死全部静默停摆。
2. **WS 单主人被永久占用**：C# 宿主与 Bridge 都缓存控制 socket，排班成功后不关闭；Python 端 owner 只在断开时释放 → 第一次排班后 Web UI/MCP 的 `/api/v1/duty/live` 永远 4409。
3. **日志只在启动时清理**：`diagnostics.py` 的 `_prune_expired_logs()` 仅在构造函数执行一次，按天分文件无大小上限。
4. **SSE 通知流占线程池线程**：`routers/notifications.py` 用 `asyncio.to_thread(queue.get, True, 10.0)`，僵尸连接可耗尽默认 executor，饿死其它 `to_thread` 调用。
5. **FileSystemWatcher 无 Error 处理**：缓冲区溢出后静默停止抛事件且永不恢复，UI 冻结在旧状态。
6. **Bridge 孤儿 meta 无限循环**：独立端崩溃残留 meta 文件 → 每 5s Disconnect+Error 刷屏；PID 复用时还会每 5s 发起注定失败的重连。
7. **配置同步 offline 预设分歧**：Python 归一化总是注入 offline 预设（`state_ops._normalize_plan_presets`），C# `NormalizeBackendDocument` 没有 → `BackendMatches` 对默认配置永远不等 → 每次触发同步都多打一次冗余 PATCH、version 空转膨胀。
8. **存量环境问题（非本次引入）**：根工程 `dotnet build Duty-Agent.csproj` 在干净树上就报 5 个 `AXN0001`（Avalonia x:Name generator 扫到 `Duty-Agent-ClassIsland-Bridge/**/*.axaml`，但 csproj 只 `<Compile Remove>` 排除了 .cs）。已用 `git stash` 干净树复现验证，与本任务改动无关。

## 3. 已完成的修改（全部在工作区，未提交）

| # | 文件 | 内容 |
|---|---|---|
| 1 | `Services/DutyPythonIpcService.cs` | 崩溃自动重启 watchdog：新字段 `_lastEngineReadyUtc/_autoRestartConsecutiveFailures/_autoRestartInFlight` + 常量（退避 5s/20s/60s，最多连续 3 次，稳定运行 ≥30min 后计数归零）；`Exited` 处理器在 fault 后调用 `ScheduleEngineAutoRestart()`（单飞 gate + 手动重启竞态保护）；启动成功时记录 `_lastEngineReadyUtc` |
| 2a | `Services/DutyPythonIpcService.cs` | `RunScheduleViaSocketAsync` 的 finally 中 `DisposeControlSocket()`——每次排班结束即释放后端单主人 claim |
| 2b | `Duty-Agent-ClassIsland-Bridge/Services/IpcBridgeService.cs` | 同上语义：bridge 的 `RunScheduleViaSocketAsync` finally 中 `DisposeSocket()` |
| 3 | `Assets_Duty/diagnostics.py` | 新增 `MAX_LOG_FILE_BYTES=32MB` 与 `_last_prune_date`；`_write` 内跨日首次写入触发 prune；超限轮转为 `.log.1`（保留一代）；prune glob 改为 `{PREFIX}-*.log*` 覆盖轮转文件 |
| 4 | `Assets_Duty/routers/notifications.py` | SSE 流改为事件循环内 `get_nowait()` 轮询（0.25s）+ 15s ping，不再占用 executor 线程 |
| 5 | `Services/DutyStateManager.cs` | FSW 缓冲区提到 64KB；新增 `Error` 事件处理器 → `RecreateWatcher()`（带重建 gate + 2s 重试）+ 强制补一次状态刷新；Dispose 时解绑 Error 并置空 |
| 6 | `Duty-Agent-ClassIsland-Bridge/Services/HealthMonitorService.cs` | 孤儿 meta 节流：`_staleMetaSignature/_staleMetaStreak`（同一签名连续 ≥3 次"进程死亡"后停止刷 Error/Disconnect，meta 变化或消失自动复位）；`NoteConnectFailure/ResetConnectFailures/_connectFailureSignature` 节流 PID 复用场景下 `Error` 分支与心跳失败路径的重连风暴；心跳成功即复位计数 |
| 7 | `Models/DutyBackendConfig.cs` + `Services/DutyBackendSettingsSyncService.cs` | 新增 `DutyBackendModeIds.Offline` 常量；`NormalizeBackendDocument` 默认表加入 offline 且归一化后强制补齐 offline 预设（镜像 Python 行为）；`NormalizePlanModeId` 增加 offline/algorithm/local/deterministic 映射；offline 默认名"离线算法" |

净变更：8 files, +357/-16。

## 4. 未完成 / 明确未修的部分

- **AXN0001 构建阻塞（存量）**：根插件工程在本机无法产出 DLL（干净树同样失败）。未修——按用户指示不开始新的独立修改。可能方向：csproj 里对 bridge 子目录的 axaml/Avalonia item 做排除（类似现有 `<Compile Remove>`），或走 CI（`.github/workflows/release.yml`）出包。
- **三个次要项有意未修**（修复风险 > 收益，需产品决策）：
  - `.prev` 单层回滚备份互相覆盖（改多代会动 rollback 语义与测试）；
  - 提醒去重 key 仅存内存（重启后 catch-up 窗口内可能重复提醒一次；持久化需两侧同步 host-config schema，易踩 C#↔Python 投影互踩坑）；
  - core.py 随机端口 bind→close→rebind TOCTOU（低概率；修复 1 的 watchdog 已能兜底此类启动崩溃自愈）。
- **运行时行为未实测**：所有修复只过了编译/单测，没做端到端验证（见 §7/§8）。

## 5. Invariant / 禁止破坏的行为

- `state.json` 只由 Python 写；C# 只读 + FileSystemWatcher 监听。
- `host-config.json` 是 C#↔Python 共享桥，**两侧 normalize 都是白名单重建 dict**：任何新字段必须两侧同时加，否则互相覆盖丢字段（这正是当初不给提醒 key 做持久化的原因）。
- `/api/v1/duty/live` 单主人（4409）语义是故意的防并发写设计；本次只是让客户端在 run 结束后释放，服务端语义一行未动。
- 启动握手 stdout 前缀 `__DUTY_SERVER_PORT__ / __DUTY_SERVER_TOKEN_MODE__ / __DUTY_SERVER_TOKEN__` 是 C# 拿端口/token 的唯一通道，格式不能变。
- auto-run 的重试预算语义：失败不消耗当日预算时保持 `last_auto_run_date=last`；放弃时置为 today。watchdog 不改变这套逻辑。
- Prompt V2 协议发送方与解析方必须配套（本次未触碰）。
- `update_host_runtime_fields` 白名单仅 `{last_auto_run_date, ai_consecutive_failures, ai_failures_date}`，不要放宽。

## 6. 已运行的测试及结果

| 测试 | 结果 |
|---|---|
| `dotnet build Duty-Agent-ClassIsland-Bridge/DutyAgentBridge.csproj` | ✅ 成功，0 警告 0 错误 |
| `dotnet build Duty-Agent.csproj` | ❌ 仅剩 **5 个存量 AXN0001**（干净树复现一致）；`error CS` 为 0 —— 本次 C# 改动编译通过（期间修过一个自己引入的 CS1503：`DutyDiagnosticsLogger.Error` 第 3 参是 `Exception?`，数据要走命名参数 `data:`） |
| `python-embed -m py_compile Assets_Duty/diagnostics.py Assets_Duty/routers/notifications.py` | ✅ OK |
| `python-embed Assets_Duty/test_auto_run_runtime.py` | ✅ Ran 15 tests, OK |

## 7. 尚未运行的测试

- 其余 Python 测试脚本（`test_core.py`、`test_state_locking.py`、`test_readiness_api.py` 等）未跑。
- 无任何针对本次行为的自动化测试：watchdog 重启、socket 释放时机、日志轮转、SSE 轮询、FSW 重建、bridge 节流、sync 收敛 —— 全部待端到端手工验证。
- `DutyAgent.Client.csproj` 未构建（本次未改动它，但依赖链上可能有间接影响）。
- C# 侧没有现成单元测试工程，`HealthMonitorService`/`DutyStateManager`/`SyncService` 的行为只有编译期保证。

## 8. 下一位 Agent 从哪里继续

优先级顺序：

1. **解决 AXN0001 让根工程本地可出包**（或明确决定只走 CI）。这是分发任何验证包的前提。
2. **端到端验证清单**（每台目标机）：
   - 杀掉 `python-embed/python.exe` → 5~60s 内日志出现 `Engine auto-restart succeeded`，功能恢复；
   - 宿主跑一次排班后，立刻从 Web UI 或 MCP 发起排班应成功（不再 4409）；
   - logs 目录旧日期文件被清；单日文件超 32MB 出现 `.log.1`；
   - 强杀独立客户端（留 meta 文件）→ Bridge 日志 ~3 次检测后安静；重启独立端自动恢复；
   - 启动/改设置后 sync 日志出现 `backend_sync_skipped_same_state` 而不是每次 `patch_applied`；
   - Web 页通知推送正常（SSE 改造回归）。
3. （可选，需先决策）处理 §4 三个次要项。

## 9. 推荐命令

```bash
# 构建
dotnet build Duty-Agent-ClassIsland-Bridge/DutyAgentBridge.csproj
dotnet build Duty-Agent.csproj        # 期望只剩 5 个存量 AXN0001，无 error CS

# Python 快速校验
Assets_Duty/python-embed/python.exe -m py_compile Assets_Duty/diagnostics.py Assets_Duty/routers/notifications.py
Assets_Duty/python-embed/python.exe Assets_Duty/test_auto_run_runtime.py

# 全量 Python 回归（可选）
Get-ChildItem Assets_Duty/test_*.py | ForEach-Object { Assets_Duty/python-embed/python.exe $_.FullName }
```

## 10. 假设与不确定项（如实声明）

- watchdog 从后台线程调用 `RestartEngineAsync()`：锁路径经过审读（`_stateLock`/TCS 重置/单飞 gate），但**未运行时验证**过与手动重启并发时的表现。
- `ComputeMetaSignature()` 用 `LastWriteTimeUtc.Ticks*397 ^ Length ^ pid`，是启发式防抖，理论上存在碰撞可能（后果仅是多等一轮 tick）。
- SSE 轮询间隔 0.25s 会带来轻微常驻事件循环开销，属有意取舍（换取零线程占用），注释里已说明。
- AXN0001 是否影响 CI 出包**未验证**（CI 可能以不同顺序/属性构建；也可能同样红着）。

## 11. 远程验证记录（Linux 验证机，2026-08-24）

> 由接手 Agent 在 Ubuntu 22.04 验证机上实测。工作区改动全部保留未提交。

### 11.1 环境事实与网络绕行

- dotnet SDK **8.0.130**（`/usr/lib/dotnet`）已装，无需 sudo；系统 python3 **3.10.12**。
- `api.nuget.org` TCP 443 被墙（DNS 正常、百度可达）。NuGet 还原改走华为云镜像——**只改了仓库外的用户级 `~/.nuget/NuGet/NuGet.Config`**，仓库内无任何源配置。
- Python 第三方依赖按 `Assets_Duty/python-embed/Lib/site-packages/` 清单经 pypi.org `pip3 install --user` 对齐（fastapi、httpx、uvicorn、mcp==1.20.0、python-multipart、sse-starlette、httpx-sse、PyJWT、python-dotenv、jsonschema、pydantic-settings、cryptography 等）；pywin32 等 Windows 专属包未装也未被测试需要。

### 11.2 AXN0001 根因与本轮新增改动（5 个文件，均未提交）

1. **`Duty-Agent.csproj` +1 行**：`<AvaloniaXaml Remove="Duty-Agent-ClassIsland-Bridge\**\*.axaml" />`。
   根因：Avalonia 11.3.6 的默认项定义在 `AvaloniaBuildTasks.props` 中 `<AvaloniaXaml Include="**\*.axaml">`，不受既有 `<Compile Remove>` 影响；bridge 子目录 5 个 axaml 被编入根程序集，其引用的 `DutyAgentBridge.*` 类型不在本程序集 → 恰好 5 个 AXN0001。排除后归零。`DutyAgent.Client` 子目录无 axaml，无需处理。
2. **CS0121 ×5 修复**：本机 Roslyn（SDK 8.0.130）对集合表达式 `text.Split([...], StringSplitOptions)` 在 `Split(char[]?, ...)` 与 `Split(string?, ...)` 重载间判定歧义（前一台机器工具链未报，属编译器版本差异）。4 文件 5 处加显式 `(char[])` 转换，行为零变化：
   - `Views/SettingPages/DutyMainSettingsPage.axaml.cs`（3039、3068 两处）
   - `Views/SettingPages/Modules/DutyMainSettingsHostModule.cs:56`
   - `Services/DutyScheduleOrchestrator.cs:1051`
   - `Services/DutySettingsRepository.cs:1098`

### 11.3 构建结果

命令均为 `dotnet build <proj> -p:EnableWindowsTargeting=true`：

| 工程 | 结果 |
|---|---|
| `Duty-Agent-ClassIsland-Bridge/DutyAgentBridge.csproj` | ✅ Build succeeded，0 警告 0 错误 |
| `Duty-Agent.csproj` | ✅ Build succeeded，0 警告 0 错误，AXN0001 = 0，产出 `bin/Debug/net8.0-windows/Duty-Agent.dll` |

### 11.4 Python 校验结果（系统 python3）

`py_compile diagnostics.py routers/notifications.py` ✅ OK。
全量 `test_*.py` 扫描（20 个文件）：**19 个全绿**，1 个非确定性失败：

| 测试 | 结果 | | 测试 | 结果 |
|---|---|---|---|---|
| auth_runtime (7) | ✅ | | no_api_key_support (7) | ✅ |
| auto_run_pure (13) | ✅ | | offline_scheduler (23) | ✅ |
| **auto_run_runtime (15)** | ❌ 见 11.5 | | readiness_api (9) | ✅ |
| build_prompt (2) | ✅ | | roster_api (3) | ✅ |
| client_lifecycle_settings (4) | ✅ | | schedule_entry_api (10) | ✅ |
| cli (17) | ✅ | | settings_api (10) | ✅ |
| config_store (7) | ✅ | | state_locking (5) | ✅ |
| core (34) | ✅ | | tool_loop (24) | ✅ |
| debt_enforcement (4) | ✅ | | execution_profiles_and_multi_agent (13) | ✅ |
| llm_transport (8) | ✅ | | mcp_api (6) | ✅（对齐 mcp==1.20.0 后全跑） |

### 11.5 test_auto_run_runtime.py 失败根因（已查明；按红线未修）

- 现象：15 用例中随机 3~5 个断言失败（连跑多次为 4/5/3/5/4），失败集合每次不同；全部集中在 check_auto_run 之后读回 host-config.json 的断言，读回值恒等于**初始值**（persist 写入不可见）。
- 根因：`runtime.py::_load_host_config()` 的 mtime 缓存 × 本机文件系统时间戳粗粒度（内核 coarse clock，~4ms jiffy 步进）。实测 /tmp 上 199 次紧邻重写中 **151 次 st_mtime_ns 完全相同** → persist 后 reload 时 stat 命中相同 mtime → 直接返回旧缓存。用 os.utime 强制 mtime 碰撞的最小复现确认缓存返回陈旧数据。
- 定性：**存量代码的平台/文件系统敏感缺陷，与本任务 8 文件改动无关**（runtime.py 不在改动集；Windows python-embed 下全绿）。潜在影响面不限于 Linux：任何 coarse-mtime 环境（如 Windows 网络盘/FAT）下 auto-run 盖章都可能滞后一个 tick。修复方向供后续决策：写路径主动失效缓存，或缓存键加 size/内容指纹。按"不要硬修"红线本次不动 runtime.py。

### 11.6 §8.2 端到端清单 —— 待 Windows 机器执行

以下各项依赖 Windows GUI / ClassIsland 宿主 / 独立客户端进程，本机无法执行，**未验证、严禁视为已通过**：
引擎崩溃 watchdog 自愈；排班后 `/api/v1/duty/live` 释放（不再 4409）；日志目录清理与 32MB 轮转 `.log.1`；bridge 孤儿 meta 节流与恢复；sync 收敛日志（`backend_sync_skipped_same_state`）；Web 页 SSE 推送回归。

### 11.7 遗留项汇总

1. ~~test_auto_run_runtime.py 的 Linux flaky~~ 已修（LR-33，§13.1）；
2. §4 三个次要项维持未修；
3. ~~CI 出包是否受 AXN0001 影响~~ 已判定：从未影响（详见 §15）；
4. 本轮新增的 csproj/cs 改动尚未提交，随原 8 文件一并留在工作区。

## 12. 长期运行边际情况审查（2026-08-24，针对 `14b8307` +8296 行未覆盖面）

> 三路并行深查（Python 运行时/通知/日志、状态锁/调度/就绪 API、C# 宿主/Bridge），高危项全部逐行人工复核。
> 共性模式：**每道防护都以"有限次尝试"收尾且终态靠人救；错误处理假设失败是瞬时的；host-config 契约两侧不对称**。
> 编号 LR-xx 用于 §13 修复对照。标注【不修】的为有意设计/需产品决策，仅记录。

### 12.1 高危

- **LR-01 日志写失败杀死两个后台线程且永不复活**（已核实）
  `diagnostics.py:88-90` `_write` 裸写无 OSError 防护；`runtime.py:199-201/403-405` 两个守护线程的循环体 except 处理器**本身调用 logger**——磁盘满/杀软锁日志文件时，OSError 从异常处理器内二次抛出终止 while 循环。`start_*_worker` 守卫使线程永不重建 → auto-run 与值日提醒永久静默停摆，且日志系统同时已坏、无任何线索。连带：`check_due_duty_reminders` 先记 sent-key 后 publish（runtime.py:443→450），publish 抛错则当天该提醒被标记已发不再补发。
- **LR-02 FSW 自愈重试不可达（死代码）**（已核实）
  `DutyStateManager.cs:106-109` InitializeWatcher 把含 `EnableRaisingEvents=true`(:104) 的全部构造包进只 Debug.WriteLine 的 catch → `RecreateWatcher:150-158` 的 catch 分支与 `ScheduleWatcherRebuildRetry()` 永远不可达。目录抖动一次后 watcher 停在"已构造但禁用"态，state.json 变更通知永久静默。**架空了本次交接的 FSW Error→Rebuild 修复（§3-5）**。
- **LR-03 watchdog 放弃后无再武装；手动重启不清零计数**（已核实）
  `DutyPythonIpcService.cs:885-890` 连续失败 ≥3 直接 return 永久 Faulted；清零仅在自动重启成功(:915)与崩溃时 uptime≥30min(:641)。环境故障持续超过退避总窗(~2 分钟)即永久放弃；用户手动 `RestartEngineAsync()`(:799) 成功后计数仍为 3 → 30 分钟内再崩则一次都不试。
- **LR-04 引擎僵死 → 排班 busy 门闩永久焊死**（代码路径已核实）
  `ReceiveControlSocketMessageAsync`(:1234-1262) 无读超时；auto-run 路径传 `CancellationToken.None`(`DutyScheduleOrchestrator.cs:213`)。进程活但不回包不断连 → ReceiveAsync 永久挂起 → `_runCoreGate` 永久占用 → 此后所有排班永远 busy，busy 被当正常码无告警。
- **LR-05 host-config 往返擦除：C# 投影整体覆写抹掉 Python 管理的 6 键**（已核实）
  `CreateProjectedHostConfig` 仅 17 字段，缺 `notification_entry / system_notifications_enabled / schedule_completion_notification_enabled / client_auto_start / client_close_action / ai_failures_date`（均在 `state_ops.py:867-910` 白名单）；`WriteCompatibilityHostConfig` 用投影整体序列化覆写文件。宿主端保存任意设置 → 6 键消失 → Python 下次 tick 归一化填默认值 → Web 页配置静默回退；独立客户端读 `client_auto_start/client_close_action` 决定自启/关窗行为（`MainForm.cs:421-424`）随之被重置。违反 `EnsureDefaultHostConfig` 特意实现的"保留未知键"契约。

### 12.2 中危

- **LR-06 重试耗尽把失败日伪造成已处理日**【不修：§5 明文 invariant + 单测锁定语义】
  `runtime.py:260-268` 放弃时置 `last_auto_run_date=today` → 次日 catch-up 视为已跑（`auto_run.py:144`）。一次 2h AI 故障 = 整周排班停在旧数据。如需改进应引入独立 `gave_up_date` 字段，两侧 schema 同步，留产品决策。
- **LR-07 提醒 catch-up 窗口固定 30 分钟**【不修：注释自述有意取舍】
  `runtime.py:36,434`。休眠跨过提醒时刻即永久漏发（课堂电脑每日睡醒常态）。可改进方向：唤醒后检测到长睡时放宽一次窗口；需产品确认。
- **LR-08 文件锁 age-only 偷锁 + release 无所有权校验 → 互斥失效**
  `state_ops.py:484-507` 只要 age≥120s 就 unlink 不管持锁者死活（`created_at` 是 wall-clock，**睡眠时间全额计入**——每晚睡眠跨持锁期则唤醒瞬间所有锁都"过期"）；`:536-555` release 无条件删文件。链条：A 活锁被偷→A release 删掉 B 的锁→C 进场→三方同入临界区，state 账本丢更新。
- **LR-09 release 竞争失败遗留孤儿锁 → 最长 ~2 分钟写入停摆**
  `state_ops.py:544-555` 重试 ~0.3s 后静默放弃；等待者 TIMEOUT(20/30s) < STALE(120s)，期间每个写请求超时失败且 auto-run 计一次失败。
- **LR-10 readiness/model-probe 在事件循环内同步阻塞、无去重节流**
  `routers/readiness.py:22-54` async def 直调同步 probe（5s 网络超时）+ `get_readiness` 先抢 config 文件锁（最长 30s）→ 阻塞整个 uvicorn 事件循环，所有 HTTP/WS 心跳停顿；连续点击测试连接逐次排队。
- **LR-11 归一化未知键丢弃 + load 差异即加锁重写**
  `state_ops.py:636-644/913-921`。版本混跑窗口（新旧 C#/Python 并存）形成双端互相擦写循环直到旧进程退出；也是 LR-05 的机制根源。
- **LR-12 C# 用本地设置文档版本号覆写 host-config version → 跨写者单调性破坏**
  `WriteCompatibilityHostConfig`: `projected.Version = Math.Max(1, settings.Version)` 与文件当前 version 无关。Web 端若干次保存后宿主端保存一次即可回绕 → 陈旧 patch 误过/误报乐观并发冲突，丢失更新。
- **LR-13 WS 连接失败路径泄漏 ClientWebSocket（双份）**
  host `DutyPythonIpcService.cs:1190-1201`、bridge `IpcBridgeService.cs:731-741`：ConnectAsync 抛出既 Dispose 也未赋值。引擎反复重启期每次失败连接漏 1 句柄。
- **LR-14 手动重启在 UI 线程同步阻塞 ~5s**
  `DutyPythonIpcService.cs:1501-1509` 的 `shutdownTask.Wait(2000)`+`WaitForExit(3000)` 经 `StopAsync` 在 Avalonia UI 线程执行（`DutyMainSettingsPage.axaml.cs:3739`）。恰发生在引擎假死的最坏时机。
- **LR-15 引擎 Faulted 后设置同步无限 5s 重试风暴**
  `DutyBackendSettingsSyncService.cs:168-188` fail→Delay(5s)→continue 无退避无上限；叠加 LR-03 后一天 ~7 万条日志/事件落盘。
- **LR-16 C# 日志 prune 仅启动执行一次；bridge 日志完全无轮转**
  `DutyDiagnosticsLogger.cs:104-115/117-138` 只切名不删除；`bridge Diagnostics.cs` 按 天追加无上限。数周 GB 级膨胀（LR-15 会加速）。
- **LR-17 Bridge SSE 流快速断连重连竞态**【推测：依赖时序，未实测复现】
  `DutyNotificationProvider.cs:54-61`（旧任务未退出时新流直接 return）、`:92-103`（Cancel 后立即 Dispose CTS，任务可能 ObjectDisposedException 静默 fault）→ 一次快速重连后推送永久死亡至下次状态翻转。

### 12.3 低危（记录在案）

LR-18 publish drop-oldest 二阶竞态整体吞事件（runtime.py:360-368）；LR-19 monotonic 跨休眠依硬件而定，唤醒初期 bridge TTL 判定短暂失真（秒级自愈）；LR-20 custom catch-up 无 31 天上限与 docstring 矛盾（auto_run.py:138-140，放假两月开机即补跑占锁数分钟）；LR-21 时钟回拨使 auto-run/提醒冬眠至追平（自愈）；LR-22 PID 复用误判孤儿锁进程存活（有 age 兜底）；LR-23 `.prev` 备份无 fsync，断电后半截 `.prev` 使 rollback_state 永久失败且主文件损坏时不自动切 .prev；LR-24 空 `client_change_id` 多 run 相互覆盖致取消失联（routers/duty.py:266,279）；LR-25 23:59 启动的排班跨午夜后 start_date 落在昨天（观感问题）；LR-26 schedule_pool day 字段中英文混存（postprocess.py:8 vs state_ops.py:1446）；LR-27 give-up 路径残留已死 Process 不 Dispose（:604-655）；LR-28 双 python 进程并存期日志行交错（diagnostics.py:89-90 两次 write）；LR-29 `AUTO_RUN_CATCHUP_MAX_LOOKBACK_DAYS` 死常量与 auto_run.CATCHUP_MAX_LOOKBACK_DAYS 靠注释同步（runtime.py:40）；LR-30 全静默配置下 prune 永不触发（diagnostics.py:81-84，目录上限 ~900MB 有界）；LR-31 host-config 脏数值令 auto-run 每 60s 抛异常刷 ERROR（runtime.py:224-225 裸 int()）；LR-32 bridge 每 5s tick 新建 Process 不 Dispose（HealthMonitorService.cs:192-204，finalizer 压力型软泄漏）；LR-33 mtime 缓存粗粒度文件系统下 persist 后读回陈旧值（§11.5 已证，Linux flaky 根因）。

### 12.4 已核查排除嫌疑的点

notification_history 有界(50)；sent-keys 逐日重建；月末/闰年由 monthrange 钳制正确；MM-DD 跨年解析正确；schedule_pool 整体替换有界；config version 内容比较才 +1 不空转；LLM 调用不持文件锁；全仓库无 Environment.TickCount（无 49 天回绕）；stdout/stderr 读取器随 Exited→Dispose 全链路释放、握手 15s 超时兜底；正常重启循环句柄数恒定；sync 收敛是内容对比非 mtime（时钟偏差无碍）。

## 13. 长期运行修复记录（2026-08-24，与 §12 编号对应）

> 修复原则：不改任何 invariant（§5）——4409 单主人语义、握手前缀、auto-run 重试预算语义（放弃置 today，LR-06 仅记录）、Prompt V2 均未触碰。
> 验证：`dotnet build` 两工程 0 警告 0 错误（EnableWindowsTargeting）；Python 全量 test_*.py **20/20 通过**（含此前 flaky 的 test_auto_run_runtime 连跑 3 次全绿）。

### 13.1 Python 侧

| 编号 | 修复内容 |
|---|---|
| LR-01 | `diagnostics._write` 全路径 try/OSError 防护，失败降级为限频(60s)stderr 提示；两个后台循环的 except 处理器本身再包 try/pass——日志系统故障不再能杀死 auto-run/提醒线程 |
| LR-28 | 日志行改单次 `write(line+"\n")`，消除双进程并存期半行交错 |
| LR-33 | `reload_host_config()` 先清空 mtime 缓存再读——自身 persist 后不再可能读回陈旧值（Linux flaky 根因，test_auto_run_runtime 由随机 3~5 失败转为稳定全绿） |
| LR-31 | 新增 `_safe_int()`：host-config 脏数值（如 `"3次"`）不再令 auto-run 每 60s 抛 ValueError 刷屏停摆 |
| （LR-01 连带） | 提醒 sent-key 改为 publish 成功后再登记：发布抛错时下个 tick 窗口内可重试，不再当天永久漏发 |
| LR-18 | publish drop-oldest 加一次重试：get/put 间隙被并发填满时事件不再整体丢失 |
| LR-24 | WS 空 `client_change_id` 的 schedule_run 自动生成唯一 id，多 run 不再互相覆盖注册导致取消失联 |
| LR-10 | readiness/model-probe 路由改 `asyncio.to_thread` + asyncio.Lock 单飞：5s 探测/30s config 锁等待不再冻结事件循环 |
| LR-08 | 锁策略分层：持锁者已死→立即偷；PID 不可读且 age≥stale→偷；age≥900s 硬上限→无论如何都偷。**age-only 偷活锁已移除**（睡眠跨持锁期不再触发互斥失效） |
| LR-09 | `release_file_lock` 所有权校验：锁内 PID 非本进程则不动他人锁（切断"偷活锁→误删他人锁→三方进临界区"级联）；PermissionError 重试窗 0.3s→1.05s |
| LR-11(host-config) | `_normalize_persisted_host_config` 白名单外的未知键原样保留——升级/混跑窗口不再互相擦写循环。config.json 为 Python 独占文件，维持白名单不变 |

### 13.2 C# 侧

| 编号 | 修复内容 |
|---|---|
| LR-02 | `InitializeWatcher` 改返回 bool（吞异常改为上报）：Rebuild catch 与重试链真正可达；首次武装失败也进入重试；重试退避 2s→60s 封顶、成功归零 |
| LR-03 | watchdog 放弃分支改 30min 冷却后清零预算重新武装（每周期仅 1 条 ERROR）；手动 `RestartEngineAsync` 成功后清零计数——"手动救活后 30 分钟内再崩立即放弃"消除 |
| LR-04 | 控制通道收包加 **15min 空闲超时**（每收到数据即重置，长跑 LLM 任务不受影响）；超时抛 TimeoutException 并归入 recoverable——僵死引擎不再把排班门闩永久焊死在 busy。Bridge 同步实现 |
| LR-05 | `WriteCompatibilityHostConfig` 改**合并保留**写入：投影字段覆盖、文件已有未知键（notification_entry/client_auto_start 等 6 键及其它）全部保留——宿主端保存不再静默重置 Web 页配置与独立客户端行为 |
| LR-12 | version 改为 `max(settings.Version, 文件当前version+1)`：跨写者单调性恢复，乐观并发不再被回绕击穿 |
| LR-13 | host 与 bridge 的 WS ConnectAsync 失败路径补 Dispose，崩溃重启循环不再每次泄漏一个 ClientWebSocket |
| LR-14 | `StopAsync` 真 async 化：阻塞的 shutdown（最长~5s）移入线程池，引擎假死时设置页重启不再冻结 Avalonia UI 线程 |
| LR-15 | 设置同步失败退避 5s→160s 指数递增，成功或新编辑到达即复位——Faulted 期从每天 ~7 万条噪声降为有限次尝试 |
| LR-16 | `DutyDiagnosticsLogger` 跨日首写触发 prune（镜像 Python 策略），Configure 切目录强制重 prune；bridge 日志新增 16MB 单代轮转 `.log.1` + 14 天按日清理 |
| LR-17 | Bridge SSE 流生命周期重写：任务自持 CTS 释放、外层兜底 OperationCanceledException（不再静默 fault）、快速断连重连经 ContinueWith 链式重启 + 代际校验丢弃陈旧链 |
| LR-32 | 三处进程探活（HealthMonitor/IpcBridgeService/Plugin.cs）补 `using`——每 5s 一个 Process 对象的 finalizer 压力消除 |

### 13.3 有意不修（记录于 §12，需产品决策）

LR-06（放弃日写 today 属 §5 invariant+单测锁定）、LR-07（30 分钟提醒窗口为注释明示取舍）、LR-19/20/21/23/25/26/27/29/30 低危项。

## 14. CLI 边际情况审查记录（2026-08-24，`cli.py` 全 1112 行逐段审读）

> 按任务安排本批**只排查+持久化，不改码**。高危项已逐条人工核实行号。
> 背景事实：捆绑 httpx 0.28.1 中 `InvalidURL(Exception)` **不是** HTTPError/ValueError/OSError 子类；readiness 顶层结果只有 `ready/checks/next_steps` 键、无 `status` 键。

### 14.1 高

- **CLI-H1 doctor 未就绪仍退出码 0**（已核实行号）
  `cli.py:1107` 仅当顶层 `result["status"]=="error"` 才返回 1；doctor 结果无该键 → roster 为空/模型探针不通（`ready:false`）时 exit 0。CI 里 `doctor && deploy` 会带病继续。test_cli.py 无 doctor 覆盖。
  修复建议：handler 对 `ready is False` 返回专用非零码（如 3）并在 describe 声明。
- **CLI-H2 `httpx.InvalidURL` 未捕获 → 裸 traceback 泄内部路径**
  `cli.py:261-270/1103` 的 except 链不含 InvalidURL。`--port -5`、`--base-url http://127.0.0.1:99999` 等拼出非法 URL 时裸崩：stdout 无 JSON、"恒一 JSON"契约破裂、脚本绝对路径（含用户目录）随 traceback 打出。
  修复建议：request 层补 `except httpx.InvalidURL` 转 RuntimeError，或 main 兜底 `except Exception`。
- **CLI-H3 serve 的 Popen→write_pid 窗口被打断 = 孤儿分离进程**（已核实行号）
  `cli.py:621`(Popen, DETACHED_PROCESS) → `:649`(_write_pid)，中间隔着最长 30s 的 `_wait_health`。等待期 Ctrl+C/断电 → 后端已在后台占 8765 但 pid 文件未写 → status 报 managed:false、stop/force 均无法回收。
  修复建议：Popen 成功即写 pid，健康失败路径再删。

### 14.2 中

- **CLI-M1 陈旧 pid + Windows PID 复用 → `taskkill /T /F` 可能误杀无关进程树**（`cli.py:476-518`）。修复建议：taskkill 前校验进程映像名含 python/core.py。
- **CLI-M2 stdin 编码不对称**（`:1070-1074` 只 reconfigure 输出）：管道输入按 cp936 解码，UTF-8 中文指令经 `plan-prompt --instruction -` 静默乱码入流水线。修复：对称 reconfigure stdin。
- **CLI-M3 doctor 对非 loopback base_url 也先在本机起后端**（`:717-734`→`:579/:606`）：远端宕机时白起一个本机后端又杀掉，诊断文案指向错误地址。修复：解析出非 loopback 就不做 bootstrap。
- **CLI-M4 KeyboardInterrupt 未处理**（`:1100-1108` 不含之）：SSE 长跑 Ctrl+C 得 traceback + Windows 异常退出码，AI 调用方拿到协议外输出；与 H3 叠加成孤儿。修复：外层捕 KI → emit_error exit 130。
- **CLI-M5 并发 serve 竞态**（`:583/:621/:649/:660` 无互斥）：pid/meta 最后写者胜，可能登记已崩的输家进程。
- **CLI-M6 `--timeout inf/nan` 永久挂起**（`:995` float 无界）：CI 卡死到 job 超时。修复：校验 `0 < timeout ≤ 3600`。

### 14.3 低

L1 SSE 多行 data: 不聚合且解析失败静默丢弃（`:411-421`，反向代理分帧场景，推测）；L2 UTF-16 输入文件报裸 codec 错误无指引（`:106/:118`）；L3 pythonw 下 stdout=None 时 print 裸崩（`:1070/:79`）；L4 `.dev-token` 含 BOM 生成晦涩协议错（`:225/:652`，推测）；L5 子命令拼写无近似提示、全局旗标必须前置（`:1000/:927`）；L6 `--instruction -` EOF 得空串直发后端（`:104-105`）；L7 redaction 只精确匹配 `api_key` 键名变体不全（`:61-70`）。

### 14.4 核查过确认没问题的方面

usage error 全部走 `_JsonErrorParser` 输出 JSON+exit 2；15 个子命令 --help 完整；无 input() 交互循环（无 EOF 死循环面）；CWD 无关性成立；数据目录自动创建；CLI 从不直读写 config/state（全走 HTTP，无绕锁写路径）；API key 缺失报错质量好（中文 fix 字段）；超时取值合理（probe 5s/默认 120s/run≥300s）；`trust_env=False`+NO_PROXY 正确规避 Windows 注册表代理陷阱；GBK 控制台主路径中文输出正常（chcp 65001+reconfigure）；meta 文件损坏候选项逐个跳过；常规错误均已规约为单行 message。

## 15. CI 出包与 AXN0001 关系判定（2026-08-24，实机验证通过后）

**结论：AXN0001 从未影响 CI 出包——它只是本地构建阻塞。**

依据（逐条核实）：
1. `.github/workflows/release.yml` 在 windows-latest 上只执行 `build_client.bat` → `scripts/New-DutyAgentClientRelease.ps1`；
2. 发布链恰好两个工程：`DutyAgent.Client.csproj`（self-contained win-x64）与 `DutyAgentBridge.csproj`（no-self-contained），全脚本 grep `Duty-Agent.csproj` 零命中；
3. 根工程 `Duty-Agent.csproj` 是唯一曾出现 AXN0001 的工程，不在 CI 构建集内；
4. Client 为 WinForms 工程（`UseWindowsForms=true`，无 Avalonia/axaml，亦无指向根工程的 ProjectReference）——不存在间接编译路径把 AXN0001 带进 CI；
5. Bridge 工程自始至终构建为绿。

附带收益（防御性）：若未来把根工程纳入发布链，本批的 AvaloniaXaml 排除 + `(char[])` 显式转换均不依赖特定 Roslyn 版本，任何 SDK 8.0.x 下均可编译。CI 实际运行验证仍需推送 tag 后在 GitHub Actions 观察（本机 GitHub 网络不通，无法代跑）。

## 16. CLI 修复记录（2026-08-24，与 §14 编号对应）

> 验证：`test_cli.py` 由 17 例扩至 **26 例 +1 平台跳过**，连同全量 `test_*.py` **20/20 文件通过**。未提交，待实机复核。

| 编号 | 修复内容 |
|---|---|
| H1 | doctor 未就绪退出码改为 **3**（0=ready / 3=命令正常但系统需修 / 1=传输错误），describe 已声明语义 |
| H2 | `request()` 补捕 `httpx.InvalidURL`；main 增加兜底 `except Exception` → 恒一 JSON 输出，任何未知异常不再裸 traceback 泄内部路径 |
| H3 | serve 在 Popen 成功后**立即**写 pid 文件；健康等待失败路径终止进程并删除 pid——Ctrl+C/断电不再留下无 pid 的孤儿分离进程 |
| M1 | 新增 `_pid_image_is_python()`：taskkill 前用 QueryFullProcessImageNameW 校验映像名以 python 开头，非匹配/不可判定一律拒绝动手（防 PID 复用误杀无关进程树）；`_stop_managed` 同样受保护 |
| M2 | stdin 对称 reconfigure 为 UTF-8(errors=replace)——管道输入的 UTF-8 中文不再按 cp936 静默乱码 |
| M3 | doctor 解析出非 loopback 目标且不可达时直接报错返回，不再本机 spawn 后又杀掉、误导性指向远端地址 |
| M4 | main 捕获 KeyboardInterrupt → emit_error 退出码 130，SSE 长跑 Ctrl+C 不再产生协议外输出 |
| M5 | serve 健康检查通过后校验 `proc.poll() is None`：并发竞争输家（端口 bind 失败已死）不再把自己的死 pid 覆盖到赢家的注册上 |
| M6 | `--timeout` 自定义 type：非有限值/≤0/>3600 一律 usage error（JSON+exit 2） |
| L1 | SSE 解析改规范语义：空行分发事件、多行 `data:` 聚合后解析，解析失败打 stderr 警告而非静默丢弃 |
| L2 | 新增 `_read_text_bom_safe()`：UTF-16 等解码失败给出"文件可能是 UTF-16，请另存为 UTF-8"的可操作提示（@file 与 --*-file 两路径统一） |
| L3 | emit/_log 判 `sys.stdout/stderr is None`：pythonw 下优雅降级到 --out 文件，不再 AttributeError 裸崩 |
| L4 | `.dev-token` 三处读取统一 `utf-8-sig`，BOM 不再混入 Bearer token 造成深层协议错误 |
| L5 | `_JsonErrorParser.error`：invalid choice 时对已知子命令做 difflib 近似匹配追加 "Did you mean"；unrecognized arguments 提示全局旗标必须前置 |
| L6 | `-`（stdin）读到空数据即抛 ValueError("stdin provided no data")，不再把空指令发往后端 |
| L7 | redaction 键集合扩为 {api_key, apikey} 且大小写不敏感 |

新增回归测试（TestCliHardening）：doctor exit 3/0、非 loopback 不 bootstrap、timeout 边界×5、InvalidURL→RuntimeError、typo 建议、空 stdin 报错、UTF-16 指引、redaction 变体键、Windows 映像守卫拒绝杀进程。

## 17. 自动排班（非 AI 链路）审查记录（2026-08-24）

> 范围：`offline_scheduler.py` 全文 + 结算侧 `postprocess.estimate_pointer_progress` / `single_pass_executor.apply_single_pass_completion` / `llm_transport._parse_areas_section/_resolve_mmdd` / `engine.py` 路由。生成器与结算共享单点：离线跑只产出 `[areas]+[schedule]`，指针/销账完全由结算侧推导——因此两边语义对齐是本链路的正确性核心。
> 已核查无问题的方面：`offline_schedule_days` 归一化钳制 1..60（state_ops:380）；MM-DD 跨年由 `_resolve_mmdd` 按 previous_date 单调滚年正确；`build_target_dates` 迭代上界充足；debt/credit 均先经 clone+active 过滤；纯 debtor 日指针不动与结算一致；`[areas]` 别名 A0..An 合法；engine.py offline 路由正确；空花名册有明确中文报错。

### 17.1 发现

- **OF-1（高）重复区域名静默吞学生 —— 已修**
  `learn_area_structure` 只折叠空白不去重：历史池里若同时存在 `"教室"` 与 `"教 室"`（AI 生成/手工编辑残留），生成器会发出两个别名映射到同一名字。结算侧 `normalize_multi_area_schedule_ids` 的动态键循环遇到同名第二个区域直接 `continue` 跳过（postprocess.py:201-210）→ **该区域学生每天被静默丢弃**，长期表现为该区域持续缺员且无任何报错。
- **OF-2（撤销，降级为结论记录）兜底日"指针漂移"经深挖不成立**
  初判认为 fallback 不推进 cursor 会造成与 `estimate_pointer_progress` 的指针分歧并累计漂移。逐行追账后证伪：**生成器的 cursor 是临时的**——每次运行都从 `state.last_pointer` 重建 rotation，持久化指针完全由结算侧 estimate 决定；两种兜底变体产出的排班人员、顺序及最终写回的 `last_pointer` 完全一致（差异仅存在于运行期即弃的局部变量里）。不存在累计漂移，原描述有误，未做任何行为改动。真正共享的语义细节一并记录：estimate 在环上遇到 credit 持有者每次运行至多销账一次（postprocess.py:108-109 去重），多圈重复经过不叠加——AI 链路同语义。
- **OF-3（低，仅记录）被排班的 credit 持有者不销账**
  postprocess.py:103-107 匹配为 target 后 `continue`，不再进入 credit 消费分支——兜底上岗的 credit 人员服务了但不扣 credit。此为 AI 链路共享语义，改动需产品决策，本轮不动。
- **OF-4（低，设计如此）结构学习取最新池条目**：可能是未来的手工占位条目或部分编辑条目导致结构漂移；文档已声明"structure is learned, not configured"，仅记录。
- **OF-5（低，优雅降级）slots_per_day > 在岗人数**：当天缺员区域整段省略，不报错。可接受，记录在案。

### 17.2 修复

- OF-1：`learn_area_structure` 折叠名合并计数（同名区域视为同一区域、headcount 相加），从源头消除别名重名 → 结算不再静默丢人。
- 新增回归测试：重名合并（前导/尾随空白变体归一）。
- OF-2 撤销说明见上；其余为记录项。

### 17.3 附带发现与加固

- **test_cli 一次未复现的偶发失败**（`--token: expected one argument`，v5 扫描中出现 1 次；随后直接跑×5、隔离跑、插桩跑全部通过）。防御性加固：`Connection` 的 token 统一经 `_sanitize_token()`——空值/选项样值（`-` 开头）/含空白一律视为无 token，杜绝此类瞬态垃圾值在 argparse 层产生远离根因的报错。
- 已知路径遮蔽陷阱（记录，未改）：以 `python3 -m unittest Assets_Duty.test_cli` 方式运行时，CWD 先于 Assets_Duty 入 path，仓库根的 `orchestrator.py` 会遮蔽 `Assets_Duty/orchestrator/` 包导致 `ModuleNotFoundError`。直接脚本方式（现有测试约定）不受影响。

## 18. 状态管理全面审查记录（2026-08-24）

> 范围：`state_ops.py` 的 state/config/host-config 三文件生命周期（load/update/save/rollback/.prev 备份）、账本映射层（normalize/clone/increment/decrement/conflict-resolution）、C# `DutyStateManager` 读取路径。
> 核查无问题的方面：账本键 int 归一完备（JSON 字符串键在所有入口经 normalize_count_map 转换）；decrement 地板为 0 不出负数；resolve_debt_credit_conflicts 债务优先；update_state 的锁内 load-modify-write 与 os.replace 原子性组合正确（读方永远看到完整旧或新）；未知字段透传前向兼容；last_pointer 钳制；rollback 单代消费语义与 §4 记录一致（维持产品决策不动）。

### 18.1 发现

- **ST-1（高）state.json 损坏时不回退 `.prev`，双侧皆然**
  Python：`load_state` 的 `json.load` 对损坏主文件直接抛异常 → 所有排班运行/更新失败，而 `.prev` 明明就在旁边。C#：`ReadStateFileUnsafe` 重试耗尽后**静默返回空 DutyState**——UI 上花名册账本/排班无声消失，无任何痕迹。损坏虽因原子写而罕见（外因：磁盘坏块/手工编辑/断电叠加），但长期运行是必然事件，且后果是核心功能瘫痪/数据不可见。
- **ST-2（高）config.json / host-config.json 损坏零恢复路径**
  两个配置加载器对 JSONDecodeError/OSError 均直接抛出。config 是启动关键（load_config 失败 → 引擎全挂）；host-config 损坏 → 15s/60s 两个 poller 每个 tick 永久失败。唯一解法是人肉删文件——且删除即丢 API key/方案配置。无人值守场景下这是永久性瘫痪。
- **ST-3（中）`.prev` 备份非原子复制**
  `shutil.copy2` 直写目标：复制中途断电留下半截 `.prev`；若随后主文件再损坏，rollback 会加载到垃圾（LR-23 的收窄版——原条目只提 fsync，实际更大的问题是非原子替换窗口）。
- **ST-4（中）update/save 在主文件已损坏时仍会用坏文件覆盖 `.prev`**
  与 ST-1 回退联动后的新风险面：load_state 从 .prev 救回数据后，备份步骤把**损坏的主文件**复制覆盖掉最后一份完好的 .prev——自愈机制自我拆台。
- ST-5（低，随 ST-2 记录）：隔离损坏配置时无法走 ctx 日志管道（unlocked 纯函数），降级用单行 stderr 输出 + `.bad-<时间戳>` 隔离文件本身作为现场保留。

### 18.2 修复

1. ST-1(Python)：新增 `_read_state_json(path)`（解析+归一化，失败抛出）；`load_state` 主文件失败时自动尝试 `.prev` 同路读取，双败才抛。
2. ST-1(C#)：`ReadStateFileUnsafe` 在返回空对象前尝试读 `state.prev.json`（同样容错），并 Debug.WriteLine 告警。
3. ST-2：两个配置解锁加载器捕获解析错误 → 将坏文件 `os.replace` 隔离为 `<name>.bad-<epoch>`（现场可查可人工抢救）→ 返回默认值（changed=True 触发重写）。stderr 单行告警。
4. ST-3：新增 `_backup_previous_atomic(path)`：copy 到 `.tmp` 后 `os.replace`，消除半截备份窗口；update_state/save_state 统一改用。
5. ST-4：备份前校验主文件可解析（`_state_file_is_readable`），不可解析则**跳过备份覆盖**（保住最后一份完好 .prev）。
6. 新增 `Assets_Duty/test_state_recovery.py`：损坏主文件回退 .prev、损坏时不覆盖好备份、config/host-config 隔离重建、rollback 对损坏 .prev 的明确报错。

### 18.3 实施与验证

- 全部 5 项已落地（Python 4 处 + C# 1 处）；实现中发现并修正一个自引入缺陷：嵌套 except 的裸 `raise` 会把"备份缺失"的 FileNotFoundError 顶替原始损坏异常，改为 `raise main_error from None`。
- 行为注记：主文件**缺失**（非损坏）时即使存在 `.prev` 也返回全新空态——新装环境不得复活旧数据（有测试锁定）。
- C# 构建双工程 0W0E；`test_state_recovery.py` 9/9；Python 全量 **21/21 文件通过**。
- 未提交，待实机复核。

- 全部 5 项已落地（Python 4 处 + C# 1 处）；实现中发现并修正一个自引入缺陷：嵌套 except 的裸 `raise` 会把"备份缺失"的 FileNotFoundError 顶替原始损坏异常，改为 `raise main_error from None`。
- 行为注记：主文件**缺失**（非损坏）时即使存在 `.prev` 也返回全新空态——新装环境不得复活旧数据（有测试锁定）。
- C# 构建双工程 0W0E；`test_state_recovery.py` 9/9；Python 全量 **21/21 文件通过**。
- 未提交，待实机复核。

## 19. CI 集成问题审查——按 ClassIsland 插件集成视角（2026-08-24）

> 范围：`.github/workflows/release.yml` → `build_client.bat` → `scripts/New-DutyAgentClientRelease.ps1` 全链路 + ClassIsland 插件清单（manifest.yml）+ UI 构建链（npm ci/vite）+ 相关 .gitignore 交互。
> 先核查排除的嫌疑：python-embed 已入库（1615 文件，含 python.exe）→ CI checkout 有内嵌 Python；package-lock.json 存在 → `npm ci` 合法；README-client.txt / duty-cli.bat 均已跟踪；WebView2 `lib_manual` 引用路径经本地构建证实有效；powershell.exe 5.1 兼容性已被脚本刻意 ASCII 化处理；bat 退出码透传正确。

### 19.1 发现

- **CI-A（高）版本戳永久硬编码 0.50.0**
  `manifest.yml version: 0.50.0` 与两个 csproj `Version=0.50.0` 全部写死；发布脚本/workflow 均不注入版本。后果：无论打什么 tag，ClassIsland 看到的插件版本永远不变——**插件升级检测失效**（同版本号不触发更新提示），用户与 CI 产物也无法区分是哪个发布。这是按 ClassIsland 插件机制最直接的集成缺陷。
- **CI-B（中）Release 资产收集依赖 LastWriteTime 启发式**
  workflow 用"最新的两个 zip"凑主包+后端包。一旦未来新增第三种包、或文件系统时间戳精度异常，就会拿错资产且无告警。
- **CI-C（高，较初审升级）全局 `*.html` 忽略连 UI 构建入口一并挡掉 → release 工作流从未可能绿**
  初查只发现 `Assets_Duty/web/index.html` 缺失；本地真实执行 `npm ci && npm run build` 后暴露更深一层：**Vite 的入口 `duty-agent-ui/index.html` 同样被 `*.html` 规则挡在库外**（全新 checkout 无此文件），vite 直接 `UNRESOLVED_ENTRY` 失败。即 14b8307 引入的 release 工作流在全新环境上**第一步 npm build 就必炸**——这回答了 §10/§15 悬置的"CI 是否红着"：是，且从未绿过。次要后果才是 SkipWebBuild / 克隆即 publish 场景缺 web 回退。
- **CI-D（低）workflow 无 concurrency 控制**：tag 触发与手动 dispatch 并发时会互相竞争创建 GitHub Release。
- CI-E（信息）：Compress-Archive 的 2GB 上限对当前体量（自包含客户端 + python-embed）仍有余量；`vue-tsc -b && vite build` 在 CI 做全量类型检查属合理成本，保留。

### 19.2 修复实施

1. ✅ CI-A：workflow 新增 Compute release version 步骤（仅 tag 触发剥离 `v` 前缀并校验格式，dispatch 保持默认）；经 bat `%*` 透传 `-ReleaseVersion`；ps1 双 publish 追加 `/p:Version=`，staging 版 manifest.yml `version:` 行同版改写（源文件不动）。
2. ✅ CI-B：collect 改显式角色匹配——`*-backend.zip` 为后端包、其余最新为主包，缺失即 throw。
3. ✅ CI-C：`.gitignore` 增加两条 `!` 白名单；新建标准 Vite 入口 `duty-agent-ui/index.html`（root=./、base='./'、#app 挂载点均与 vite.config/src/main.ts 核对）；本地 `npm ci`(256 包) + `npm run build` 真实跑通产出 dist，`dist/index.html` 同步入库为 `Assets_Duty/web/index.html` 回退基线。
4. ✅ CI-D：`concurrency: release-${{ github.ref }}` + cancel-in-progress。

验证边界（如实声明）：本机无 pwsh，ps1/workflow 改动为静态审读级验证（语法保守写法、PS5.1 兼容）；UI 链路与两入口文件为本机 node22 实测通过。端到端以推送 tag 后的 Actions 运行为准。
