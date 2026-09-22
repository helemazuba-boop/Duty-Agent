# ClassIsland 桥接插件融合度改造计划

**已查实的宿主 API 事实（改造依据，均来自官方源码/文档/示例）**
- 设置模式：`PluginBase.PluginConfigFolder`（宿主固定分配 `<AppConfig>\Plugins\<id>`）+ `ConfigureFileHelper.LoadConfig/SaveConfig`（ClassIsland.Shared.Helpers）+ INotifyPropertyChanged 自动保存；设置页 `SettingsPageBase` + `[SettingsPageInfo]`；主题用 `DynamicResource MaterialDesignBody/MaterialDesignPaper` + `FontFamily={StaticResource HarmonyOsSans}`（官方 ExamplePlugins/PluginWithSettingsPage 验证）
- 共享 meta 根 = PluginConfigFolder 上两级 `\DutyAgentBridge\`（宿主 PluginService.cs 固定布局，可确定性推导）
- SDK 最新 2.1.1.1（插件钉 2.0.0.2，宿主 2.1.0.1）；`AddSettingsPageGroup` 在现行 SDK 已是公开 API
- `ILessonsService.PostMainTimerTicked/OnClass/OnAfterSchool` 为每分钟/行为事件源；`IExactTimeService.GetCurrentLocalDateTime()` 提供 NTP 校准时间
- `NotificationContent.IsSpeechEnabled` 默认 true（走用户语音设置）——插件硬编码 false 是主动关闭
- 后端 SSE 事件 type 仅 4 种（test/schedule_completed/schedule_failed/duty_reminder），**无快照变更事件**（save/rollback 均不发通知，Web UI 也是 20s 轮询）；WS 纯请求-响应；客户端 NotificationStreamClient 用 45s 空闲超时 + 2s→30s 指数退避（参照实现）

---

## 阶段 0 — SDK 升级与基线
- `DutyAgentBridge.csproj`：`ClassIsland.PluginSdk` 2.0.0.2 → **2.1.1.1**（保留 `ExcludeAssets runtime`）；`apiVersion 2.0.0.0` 不变（宿主 ≥2.0 兼容）
- 构建 + 部署到 ClassIsland（停 CI→替换→起 CI），确认插件加载

## 阶段 1 — 宿主融合（P0：路径/设置）
1. **删整条自建路径猜测链**（Plugin.cs:111-319 的 BridgePaths：env→程序集上溯→`GetProcessesByName("ClassIsland")`→appdata→guess）：
   - `PluginConfigFolder` ← 宿主 `PluginBase.PluginConfigFolder`
   - 共享 meta = `GetFullPath(PluginConfigFolder + ..\..\DutyAgentBridge\.duty-agent-meta.json)`；`CLASSISLAND_CONFIG_PATH` env 保留为覆盖项
   - 日志目录移入 `PluginConfigFolder\logs`；删 `BridgeConfigCandidate/Discovery` 模型
2. **设置入宿主体系**：`BridgeSettings` 改 ObservableObject(INPC)；Plugin.Initialize 用 `ConfigureFileHelper.LoadConfig/SaveConfig`（PropertyChanged→自动保存，官方模式）；删 IpcBridgeService.cs:102-131 自管 JSON；首启迁移旧 `bridge-settings.json`（读值并入后删除）
3. **设置页**：XAML 换官方主题模板；新增可编辑卡片（自动连接/连接超时/健康检查间隔/完成通知开关）绑定 Plugin.Settings；"测试排班"加确认框；版本号取 `Plugin.Info` 不再硬编码；路径诊断区简化
4. **删 InjectService 反射 hack**（Plugin.cs:321+）→ 直调 `services.AddSettingsPageGroup(...)`

## 阶段 2 — 行为绑定（P0：推送/缓存/规则）
5. **后端小改（已获准）**：runtime.py 新增 `publish_snapshot_changed(reason)`（type=`snapshot_changed`，targets=["classisland"]，route=/schedule）；调用点：command_service 的 save_schedule_entry 成功后、rollback_schedule 成功后、update_roster 后、update_config 成功后；新增 `Assets_Duty/test_notifications_push.py`（CI 同款单文件 unittest，验证 4 个变更点各发一条）
6. **新增 DutyStateCache 单例**：连接成功后初始全量拉一次；SSE 收到 snapshot_changed → 500ms 防抖后重拉；断流期间 60s 低频兜底；暴露 `event SnapshotUpdated` + `GetTodayItem()`（用 IExactTimeService 判日，时钟源可注入）
7. **规则去网络化（P0 核心）**：DutyRuleHandlerService.HandleTodayAssignedRule 改为读缓存纯内存判断，删除 `Task.Run(...).Result` 同步 HTTP（DutyRuleHandlerService.cs:43-58）
8. **DutyComponent 去轮询**：删自有 DispatcherTimer 与 DateTime.Now；订阅 SnapshotUpdated + `ILessonsService.PostMainTimerTicked`（跨天/分钟 tick 兜底）；渲染逻辑保留，RefreshIntervalSeconds 设置项随迁移废弃

## 阶段 3 — 通知流内聚 + 能力接线
9. **通知流移入 IpcBridgeService**：一个持久 stream task（45s 空闲超时 + 2s→30s 指数退避，参照 NotificationStreamClient），暴露 `event Received(DutyNotificationEvent)`；DutyNotificationProvider 删 generation/chaining 复杂逻辑（:56-150）只订阅事件
10. **接线**：DutyRunScheduleAction.OnInvoke 成功/失败后调 `PublishScheduleCompleted`（尊重 PublishCompletionNotification 设置）；删除死代码 `PublishAutoRunTriggered`/`PublishDutyReminder`（提醒已由后端 SSE duty_reminder 承载）；`Dispatcher.UIThread.Invoke`→`InvokeAsync`；删两处 `IsSpeechEnabled=false` 硬编码（恢复用户语音控制）

## 阶段 4 — 主题与生命周期
11. **组件主题化**：DutyComponent.axaml 前景绑定 MaterialDesignBody，错误态用主题画刷，删 Brushes.Red/Black；FontSize 等设置渲染时实时生效
12. **生命周期补全**：组件/页面改构造注入（删 `IAppHost.GetService` 字段初始化器）；`SystemEvents.PowerModeChanged(Resume)` → `bridge.Reconnect()`；心跳失败日志按签名去重（30s 窗口）；AppStopping 顺序复核（DI 单例 Dispose 链）

## 阶段 5 — 规范与打包
13. manifest.yml 补 `readme/icon/repoOwner/repoName/assetsRoot/supportedOSPlatforms: [Windows]`；新增插件 README.md；声明 `assetsRoot: master/Duty-Agent-ClassIsland-Bridge`
14. 打包改官方一键 `dotnet publish -p:CreateCipx=true`（输出 .cipx + MD5 校验），替代裸 build 输出部署
15. Action/Rule 的 kebab id **保持不变**（改 id 会破坏已保存的自动化配置），代码注释说明

## 验证（每阶段后执行）
- 构建 0 警告；停 CI→替换→起 CI 部署流程
- 阶段1：设置项可编辑并持久化到 `<Config>\Plugins\duty-agent-bridge\Settings.json`，宿主重启后保留
- 阶段2：后端单测过；edit-entry/rollback 后组件 ≤1s 刷新（后端日志见 snapshot_changed，无轮询请求）；规则评估不再产生 /api/v1/snapshot 请求；跨天自动刷新
- 阶段3：自动化排班完成弹出通知；语音随用户设置；断流 30s 内自愈
- 阶段4：暗色主题下组件与设置页可读；唤醒后自动重连
- 全程回归：心跳 5s 稳定、targets=classisland 过滤正确、真实排班数据不受污染（测试后 rollback）

## 不做/后续
集控策略下发、URI 深链、插件自管后端（保持"独立客户端拥有后端"架构）、多语言

## 提交策略
每阶段一个 commit（消息按仓库惯例 `fix(plugin): ...`），阶段 2 的后端改动单独 commit；最终部署验证在全部阶段完成后统一做一轮端到端（含 opencode 委派 + rollback 恢复现场）