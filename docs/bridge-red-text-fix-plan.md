# 桥接插件红字 / 轮询 修复计划

> 前置：根插件已删除（`907207a`），报告里的 P5（根插件画红）/ P6（数据目录分裂）**已自动消失**。
> 本计划只针对**现存的桥接插件 + 客户端 + Web UI**。

## 一、核实结论（与报告原文的差异）

| 报告项 | 现状 | 说明 |
|---|---|---|
| P1 心跳零容错 | ✅ 仍存在 | `HealthMonitorService.SendHeartbeatAsync` 单次失败即 `Disconnect()` + Error |
| P2 无离线缓存 | ✅ 仍存在 | 桥接 `DutyStateCache._state` 纯内存；断连即红 |
| P3 not-a-bug | ❌ **报告有误** | 桥接**已**订阅推送并做 500ms 防抖重拉，60s 只是兜底。真正缺的是"安全轮询太重" |
| P4 孤儿 meta 卡红 | ✅ 仍存在 | 但**前提是客户端进程也死了**；正常退出会删 meta，卡死窗口比报告说的小 |
| P5 根插件画红 | ✅ **已消失** | 根插件已删。桥接已区分"加载中／该日无安排"两态，均非红 |
| P6 数据目录分裂 | ✅ **已消失** | 根插件已删，不存在两个 state.json |
| Web UI | ✅ 仍存在 | 20s 轮询全量 snapshot；`isError` 时红字 + 红色补排按钮 |
| 附加发现 | 🔴 **报告中未提** | `DutyStateCache.Today` 的 `AfterRefreshTime` 语义**与后端及 Web UI 相反** |

## 二、根因

红字有三个独立来源，必须分开治：

1. **心跳零容错**：一次 5s 心跳失败 = 立刻红。系统唤醒、后端正在写 `state.json`、杀软扫盘都会触发。
2. **无离线缓存**：连不上时没有任何可渲染的旧数据，只能红。
3. **日期语义不一致（新发现）**：桌面组件可能认为"该日无安排"而去 Web UI 却有排班——不是红色问题，但同属"显示不对"。

---

## 三、修复计划（4 批，按优先级）

### 批次 1 —— 心跳容错 + 三态渲染（直击红字）

**1.1 `HealthMonitorService`：连续失败才降级**

- 新增 `_consecutiveHeartbeatFailures` 与 `_bridgeHeartbeatDegraded`。
- `SendHeartbeatAsync` 失败时：
  - 计数 +1，`< HeartbeatFailureThreshold`（建议 3）→ **原位重试**（1s/2s/4s 退避），**不** `Disconnect()`、**不** 抛状态变更；
  - 达到阈值 → 才 `Disconnect()` + `BridgeStateRequested(Error)`。
- 成功时：清零计数；若此前已降级 → 主动 `BridgeStateRequested(Checking)` 触发重连并清红。
- 失败日志仍保持 30s 去重。

**1.2 `DutyStateCache`：保留旧数据**

- `RefreshCoreAsync` 的 `catch` 分支**保持 `_state` 不变**（当前也不会清，但要在断连路径上补一条：`OnStateChanged` 收到非 Connected **不**清 `_state`）。
- 新增 `LastRefreshUtc` 已有；补一个 `bool IsStale`（超过 `StaleThreshold`，建议 `max(3 × healthInterval, 30s)`）。

**1.3 `DutyComponent`：三态 + 离线态**

改 `RefreshContent()`：

```
断连 && 有缓存  → 正常渲染数据 + 追加灰字"(离线)"
断连 && 无缓存  → 红：未连接状态
已连 && State==null（首拉未完成）→ 常规色：正在获取排班数据…
已连 && 无该日条目        → 常规色：该日暂无值日安排
已连 && 有数据            → 正常渲染
```

- `ApplyTextStyle` 增加 `isStale` 参数（灰 `#9e9e9e`）；**只有当既断连又无缓存时才红**。
- 这样"后端在写盘的 200ms 里心跳抖了一下"最多显示灰字，不会闪红。

### 批次 2 —— 日期语义统一（新发现，务必做）

**2.1 `DutyStateCache.Today`**

现状是"过刷新时间 → **推进**到明天"，与后端 / Web UI 的"**回退**到今天"相反。改为直接采用后端权威值：

- `RefreshCoreAsync` 里保存 `snapshot.CurrentDutyDate`。
- `Today` 改为 `_currentDutyDate ?? 本地回退计算`（回退也按 `now.TimeOfDay >= refresh ? now.Date : now.Date-1d`，即后端语义）。
- `GetTodayItem()` 用 `Date == Today` 匹配（后端 `current_duty_date` 已是 `YYYY-MM-DD`）。
- 桥接 `Snapshot` 模型补 `CurrentDutyDate` 字段（后端 `get_snapshot` 已返回 `current_duty_date`）。

**2.2 组件兜底**

跨天刷新已由 `PostMainTimerTicked` 每分钟兜底；改为与后端一致后，跨 08:00 的行为才正确。

### 批次 3 —— 降负载（低风险，可与 1/2 同批）

**3.1 meta 文件改 `FileSystemWatcher`**

- `HealthMonitorService` 增加 `FileSystemWatcher`（监听 meta 的 `Changed/Created/Deleted/Renamed`，走 `ScheduleCheck` 去抖 200ms）。
- Timer 从 5s 放宽到 **15s**（仅做心跳 + 兜底），meta 存在性/变更交给 watcher。
- `IsProcessAlive` 已 `using`，无泄漏；负载下降主要在去掉每 5s 的 `GetProcessById`。

**3.2 安全轮询换轻量端点**

- `DutyStateCache` 的 60s 兜底从 `/api/v1/snapshot` 换 `/api/v1/state`（返回 `{state, mtime_ns}`）。
- 先比 `mtime_ns`，**变了才** 拉全量 snapshot（复用组件设置里的 `ComponentRefreshTime` 需另拉一次，可在 mtime 变化时一并刷新）。
- 意义：稳态下 60s 一次轻量请求替代全量（含 roster + config）。
- `IpcBridgeService` 补 `GetStateAsync()`。

### 批次 4 —— 唤醒恢复 + 边缘

**4.1 唤醒后重建 SSE**

- `Plugin.OnPowerModeChanged` 除 `Reconnect()` 外，**显式**让 `IpcBridgeService` 重建通知流（`Reconnect → Disconnect` 会 `StopNotificationStream`，连接成功再 `Start`，但要确认竞态：当前 `State` setter 里做 start/stop，需要验证 Resume 后确实重新进入 Connected）。
- SSE 静默失效 45s 保持；可选降到 30s（后端 ping 15s）。

**4.2 孤儿 meta 节流放宽**

- 阈值从 3 提到更高，或改为"指数退避重连（上限 5min）"而不是**永久停连**。
- 至少：`HandleMissingMeta()` 命中时也 `BridgeStateRequested(NotInstalled)`，让组件显示"未连接"而非停在最后一次错误态。

**4.3 Web UI 缓存（可选，优先级最低）**

- `useSnapshotQuery` 加 `placeholderData: keepPreviousData`（vue-query）→ 刷新/路由切换不闪空白。
- `isError` 时用"上次数据 + 顶部离线条"，不用红字 + 红色补排按钮。
- token 已在 `sessionStorage`，刷新不丢；这条主要是消除闪烁。

---

## 四、验证方式

| 项 | 方法 |
|---|---|
| 心跳容错 | 后端日志级别临时调高 / 手动 `POST /shutdown` 再快速拉起；或把 TTL 临时改 5s，观察是否连续 3 次才红 |
| 离线态 | 停后端（不关客户端）→ 组件应显示旧数据 + "(离线)"灰字；重启后端 → 自动恢复常规色 |
| 日期语义 | 把 `component_refresh_time` 设成刚过去的时间，对比组件显示的日期与 Web UI "今天"是否一致 |
| 降负载 | 抓 60s 内 C# 侧请求数（DevTools / 后端 access log），确认 meta stat 降频、安全轮询变轻 |
| 唤醒 | `powercfg` 进入睡眠再唤醒，观察重连耗时与是否闪红 |
| 回归 | `dotnet build Duty-Agent-ClassIsland-Bridge/DutyAgentBridge.csproj` + `DutyAgent.Client` 均 0 警告 |

## 五、风险

- **心跳容错窗口**：连续 3 次失败 = 最多约 5s×3 + 退避 ≈ 20s 才降级；期间若后端真挂了，组件仍显示旧数据（灰字）约 20s。这是**有意**的取舍——比闪红好。需要保证 TTL 与阈值匹配（后端 TTL 20s，桥接 5s 一拍 3 次 ≈ 15s，仍 < 20s，安全）。
- **日期语义改动影响规则**："今日值日匹配"规则也读 `GetTodayItem()`，改 `Today` 后规则在 08:00 前后的行为会变（变得与后端一致，属修正）。需要顺带回归该规则。
- **`FileSystemWatcher` 冷启动**：watcher 建立前 meta 可能已存在，`Start()` 里要**先立即 `CheckMetaFile()` 一次**再挂 watcher（现状已有立即检查）。
- `--no-verify` 提交仅用于无 hook 仓库；本仓库无 hook，正常提交即可。

## 六、建议落地顺序

1. 批次 1（红字）—— 用户感知最强，独立可发。
2. 批次 2（日期语义）—— 正确性 bug，建议紧随。
3. 批次 3（降负载）—— 低风险优化。
4. 批次 4（唤醒 + 边缘 + Web UI）—— 收尾。
