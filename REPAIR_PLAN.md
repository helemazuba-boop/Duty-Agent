# REPAIR_PLAN.md — 全量修复执行规格（2026-09-12）

> 依据：2026-09-11 三路并行审计（48 项发现，见会话记录；HANDOFF §20-23 为已修项）。
> 本文件是 Wave 1 并行修复的**唯一规格源**。各 agent 只改自己名下文件，不得 git commit，
> 不得改动 HANDOFF.md（总账由主 agent 在 Wave 3 撰写）。

## 0. 架构决策（用户已拍板）

| # | 决策 | 内容 |
|---|---|---|
| D1 | state.json 单写方 | 插件 C# **不再有任何 state.json 文件读写**，读取走后端 `GET /api/v1/state`（Agent A 提供），变更感知改轮询；写路径本来就不存在业务写入，仅移除 create-if-missing 直写 |
| D2 | auto-run 单点化 | 后端 `runtime.check_auto_run` 是**唯一**触发者；插件删除 `_autoRunTimer`/`TryRunAutoSchedule` 的 auto-run 分支（不再自判日期/自跑/回写 host-config）；提醒发布权归后端通知总线，插件不再直发 |
| D3 | 次要入口处置 | 删除 `Assets_Duty/desktop/`、`Assets_Duty/start_backend.py`、`build_desktop.bat`（Wave 3 主 agent 执行）；`orchestrator.py` 修复保留 |
| D4 | api_key 加密 | DPAPI（`CryptProtectData/CryptUnprotectData`，当前用户作用域），密文前缀 `dpapi:v1:` + base64；C# 写入加密，Python 读取解密（ctypes），无前缀明文键兼容读取 |
| D5 | auto-run 认领语义 | 跨进程原子认领：文件锁内"读 last_auto_run_date→非今日则原子置为今日"一步完成；当日重试改为**胜出进程内存态**重试（attempt 计数），不再依赖 host-config 失败计数做跨进程重试 |

## 1. 文件所有权矩阵（Wave 1 严格隔离）

| Agent | 独占路径 |
|---|---|
| A | `Assets_Duty/*.py`、`Assets_Duty/routers/`、`Assets_Duty/orchestrator/`、`Assets_Duty/application/`、`orchestrator.py`（根，dev 入口） |
| B | `Services/`、`Models/`、`Duty-Agent-ClassIsland-Bridge/` |
| C | `DutyAgent.Client/`、`duty-agent-ui/src/`、`Assets_Duty/web/`（构建产物同步） |
| 主 agent（W3） | `HANDOFF.md`、`REPAIR_PLAN.md`、删除 desktop 等、`Assets_Duty/VERSION` 相关 stamp 对接 |
| D（W2） | `scripts/`、`.github/`、`*.bat` |
| E（W2） | `Assets_Duty/test_*.py` 新增、首个 C# 测试工程（新目录） |

禁改：`Assets_Duty/python-embed/`、三方引用的公共协议（见 §2 契约，需改动时在报告中提出而非直接动对方文件）。

## 2. 跨 Agent 契约（并行依据，不得单方面更改形状）

- **C1** `GET /api/v1/state`（A 提供 → B 消费）：Bearer 保护；返回 `{"state": <load_state()原样dict>, "mtime_ns": <int|null>}`。B 以 `mtime_ns` 变化作为 StateChanged 去抖依据。
- **C2** DPAPI 密文（B 写 → A 读）：字符串 `dpapi:v1:<base64(CryptProtectData(utf8(plain)))>`；Python 侧 `dpapi_compat.unprotect()` 失败→置空 key + logger.warn，不抛。
- **C3** 版本单一源：A 建 `Assets_Duty/version.py`（`APP_VERSION = "0.50.0"`）；runtime/core 全部 import；W2 的 D 负责 stamp 该文件与两个 csproj、manifest。
- **C4** 通知 schema：Python `runtime.publish_notification` 的 dict 为真源；C# 消费模型（Client 与 Bridge）字段全部可空容错补齐（`created_at_iso`、`data`）。

## 3. Wave 1 任务规格

### Agent A（Python 后端，14 项）
1. `/shutdown` 优雅化：main() 持有 `uvicorn.Server` 实例，handler 置 `should_exit=True`；删除 `os.kill(SIGTERM)`；lifespan 清理因此可达（runtime.py worker join 生效）。验收：起后端 POST /shutdown，进程 5s 内退出且日志出现 lifespan shutting down。
2. SKIP_AUTH_BYPASS 可见：/health、/engine/info 增加 `auth_bypassed` 布尔；启动时 diagnostics logger WARN 一次（文件日志）。
3. `.dev-token`：仅当 `DUTY_DEV_WRITE_TOKEN=1` 才写盘（orchestrator.py 注入该 env）；否则不写。
4. 端口 TOCTOU：bind 后不 close，`uvicorn.run(..., sock=sock)` 直接传 socket（验证当前 uvicorn 版本支持）；端口行打印自 `sock.getsockname()`。
5. auto-run 原子认领（D5）：state_ops 新增 `claim_auto_run_today(data_dir, logger) -> bool`，复用 host-config 锁；`runtime.check_auto_run` 改为先 claim 再执行；当日重试改为 runtime 内存 attempt 计数（上限沿用 auto_run_retry_times）。
6. `GET /api/v1/state`（C1）。
7. api_key DPAPI 解密（C2）：新增 `Assets_Duty/dpapi_compat.py`；config 归一化处接入。
8. `version.py` 单一源（C3）：runtime.py APP_VERSION、core.py 4 处内联全部改为 import。
9. 静默 except 清理：core.py 裸 `except: pass`（input.json）→ 记录并以 error 结果返回；`orchestrator/executor.py:355、:531` → logger.error + 结果携带 degraded 标记；`llm_transport.py:282` chunk 解析失败 → debug 日志含样本；`runtime.py:393` 通知队列 drop → 计数 + 每 10 次丢弃 WARN；`routers/duty.py` WS 4401 close → 日志。
10. `orchestrator.py`（dev）：port/token 捕获后终身排空两管道；node 子进程树终止（taskkill /T）；为后端注入 `DUTY_DEV_WRITE_TOKEN=1`。

### Agent B（C# 插件/服务，9 项）
1. DutyStateManager 重写（P0-1/D1）：删除全部 state.json 文件 IO（含两处 WriteAllText 与 FileSystemWatcher）；LoadState/StateChanged 改为经 `IPythonIpcService` 调 `GET /api/v1/state`（5s 轮询 + mtime_ns 比较 + 150ms 去抖）；接口签名不变，调用方零改动；后端未就绪时返回缓存/空态并在 Debug 输出。
2. auto-run 移除（P0-2/D2）：删 `_autoRunTimer`、`TryRunAutoSchedule` auto-run 分支及其 `UpdateHostConfig` 回写；保留 `_currentDutyBoundaryTimer`（UI 刷新）；`TryPublishDutyReminderNotifications` 移除（提醒归后端总线）。
3. host-config 文件锁：新增 `Services/HostConfigFileLock.cs`（O_EXCL 协议对齐 state_ops：`.lock` 文件 + PID + 10s 陈旧锁接管）；`DutySettingsRepository` 所有 host-config 写入点接入。
4. normalizer 归一：抽取单一 `DutyBackendDocumentNormalizer`，DutySettingsRepository 与 DutyBackendSettingsSyncService 共用；默认 preset 对齐 Python（含 offline），别名合并。
5. api_key 加密（C2/D4）：SecurityHelper 增 DPAPI P/Invoke；settings.json 与内存投影写入即加密；旧明文键读取兼容并在下次保存时升级为密文。
6. DutyPythonIpcService：新增 HTTP 就绪探测（轮询 /health ≤15s）后再置 Ready；SSE 解析 catch（:1126/:1138）→ 记日志 + LastErrorMessage；EnsureProcessBoundToJob 失败 → ERROR 日志；SendJsonAsync 非 2xx → LastErrorMessage 更新。
7. Bridge 通知模型对齐（C4）。
8. 通知/设置键所有权修复：插件投影覆盖列表移除 `auto_run_*`/`duty_reminder_*`（这些键 owner=host-config/后端）。
9. cli pid 竞态防护注释级修复不做（cli.py 属 A，报告转达）。

### Agent C（独立客户端 + 前端，10 项）
1. BackendProcessManager watchdog：启动完成后进程 Exited → 有界重启（3 次，退避 2/8/30s，稳定运行 30min 重置计数）；新增 `BackendRestarted` 事件；MainForm 订阅→ 以新 WebAppUrl 重导航 WebView + 重启 NotificationStreamClient。
2. host-config 损坏防护：解析失败→先复制 `host-config.json.corrupt-<timestamp>` 再写默认；写一行日志到 `data\logs\client-<date>.log`（新增轻量文件日志助手）；**不再无条件重置** `access_token_mode`/`static_access_token_verifier`（仅键缺失时补默认 dynamic）。
3. 子进程环境清洗：StartAsync 剥离 `SKIP_AUTH_BYPASS`、`DUTY_DEBUG_*`。
4. WebView DevTools：默认关闭，`Debugger.IsAttached` 或 `DUTY_WEBVIEW_DEVTOOLS=1` 时开启。
5. NotificationStreamClient：重连/错误写 client 日志；后端 15s ping 校验（>45s 静默即重连）；`NotificationEvent` 模型补 `created_at_iso`/`data` 可空字段（C4）。
6. 移除 `RedirectStandardInput`。
7. 前端 http.ts：401 文案改"登录状态已失效，请重启 Duty-Agent 客户端或在设置页重新检测"；500 透出后端 `detail`。
8. useScheduleWebSocket：运行期 90s idle 超时→错误 + isRunning 复位；onmessage 解析失败记 console.warn。
9. 连接检测合一：新增 `src/composables/useBackendConnection.ts`（20s 轮询单源），AppLayout / SettingsPage / Dashboard 全部改消费它；SSE/WS 鉴权头走共享 helper。
10. `npm run build` + dist 干净同步 `Assets_Duty/web`（grep 断言无 `__DEV_TOKEN__`）。

## 4. 统一完成定义（DoD）

- A：`py -3.13 test_auth_runtime.py` 等既有测试全绿 + 手动 curl 验证 /shutdown、/api/v1/state、/health；附命令输出。
- B：`dotnet build Duty-Agent-ClassIsland-Bridge + 主解决方案` 0 error（注意 net8.0-windows）。
- C：`dotnet build DutyAgent.Client` 0 error + `npm run build` 0 error + 产物断言。
- 三方：不 commit、不改 HANDOFF.md、不改他人文件；报告"改动文件清单 + 每项状态 + 偏差说明"。

## 5. Wave 2 / 3（概要）

- D：version stamp 接 C3、python-embed 导入冒烟、web 产物新鲜度断言、CI test job（windows-latest 跑 py 测试 + dotnet build）。
- E：回归测试固化（管道排空、SuicideWatch、auto-run 并发认领、host-config 并发写、state 端点、dev-token 门控）。
- 主 agent W3：删 desktop/、start_backend.py、build_desktop.bat；全量构建 + 实机验收（401/刷新/杀父自退/排班失败可见）；HANDOFF §24 总账。
