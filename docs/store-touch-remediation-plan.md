# 上架防呆与 Win10 触摸适配修复计划（修订版）

> ⚠️ **合入状态（2026-10-21 合并两个补丁时核对）**
> 这份文档来自另一台机器上的分支，该分支只导出了两个补丁：
> `fix(client): 自启 Run 值作用域与跨形态数据目录互斥` 与
> `docs,ci: 上架/触摸修复计划落档与本机验证记录`。
> **实际合入本仓库的只有「批次 3」**：Run 值名按 PFN 作用域、旧值迁移清理、
> startupTask 接管时清 Run 痕迹、数据目录 owner 互斥、`AutoStartRunValueTests`。
> 批次 0（`DUTY_CHROME_PROBE` 探针）、批次 1（触摸交互）、批次 2（`-Publisher`
> 参数 / `-ForStore` 硬校验 / 证书按发布者哈希缓存 / WebView2 运行期探测）
> **在本仓库中不存在**——按标识符核对均为零命中。下文相关条目请当作"另一条线的
> 计划与本机记录"，不要当作本仓库现状。

> 状态：**已落地代码部分并完成本机验证**（2026-10-17），真机验证与商店提交流程仍待办。
> 上游文档：`docs/msix-store-adaptation-plan.md`（包身份改造）、`README.md`（商店章节）。
>
> 本机验证（.NET SDK 8.0.426 + Node 20.20.2，详见文末「本机验证记录」）：
> client 32/32、bridge 9/9、vitest 29/29 全部通过；客户端与测试工程零警告编译；
> `vue-tsc + vite build` 通过；`Build-MsixPackage.ps1` 在 Windows PowerShell 5.1 下
> 解析与发布者/证书派生逻辑实测通过。

## 这一版为什么重写

第一版计划（同日提出、未落盘的初始提议）存在四类问题，本版逐条修正：

| # | 第一版的问题 | 本版处理 |
|---|---|---|
| 1 | 把「触摸拖拽失效」「触摸缩放热区过小」当成已确认的 P0 缺陷（实际只是静态推断） | 降级为**待真机复现**；先落地 `DUTY_CHROME_PROBE=1` 探针把两条命中路径打进日志 |
| 2 | 声称「MinVersion 提到 10.0.19041 可保证 WebView2 可用」 | 撤回。微软官方文档明确：Win11 预装；Win10 绝大多数已装但**仍有少量缺失**，建议运行期检测并引导安装。修复 = 检测 + 引导，而非提版本 |
| 3 | 建议关闭 WebView2 默认上下文菜单 | 撤回。触屏没有 Ctrl+V，长按菜单里的「粘贴」是填写 API Key/名单的主要入口 |
| 4 | 遗漏两个有实锤证据的坑：MSIX 自启丢 `--silent`；zip 版与商店版共享数据目录会双写 | 双写 → 批次 3 修复（数据目录 owner 互斥）；自启静默 → 本轮接受并记录为已知差异（见「明确决策」） |

另一处技术修正：触摸模态循环不能用 `SendMessage` 进入（`BeginNativeMove` 注释里已有线上教训——会同步重入消息泵），统一改为 `PostMessage`。

## 批次与状态

| 批次 | 内容 | 状态 |
|---|---|---|
| 0 | 探针：`DUTY_CHROME_PROBE=1` 记录 native / web 两条拖拽缩放路径 | ✅ 已落地 |
| 1 | 触摸交互：`drag-window-touch`（SC_MOVE，PostMessage）、`any-pointer: coarse` 热区与 44px 目标、保留粘贴菜单、`touch-action`/`overscroll-behavior` | ✅ 已落地（待真机） |
| 2 | 商店防呆：`-Publisher` 参数、`-ForStore` 硬校验、证书缓存按发布者派生、WebView2 缺失引导 | ✅ 已落地 |
| 3 | 自启与并存：Run 值名按 PFN 作用域、旧值迁移清理、startupTask 接管时清 Run 痕迹、数据目录 owner 互斥 | ✅ 已落地 |
| 4 | 工具链 spike：恢复 `desktop6:startupTask`（完整 Windows SDK）、WACK 认证工具跑通 | ⏳ 待办 |
| 5 | 商店材料与回归：隐私政策、截图资产、v1→v2 覆盖安装、Win10 触摸真机矩阵 | ⏳ 待办 |

## 落地记录（2026-10-17）

### 批次 0 —— 探针

- `DutyAgent.Client/MainForm.cs`：`DUTY_CHROME_PROBE=1` 时，web→host 的 `drag-window(-touch)` / `resize-window(-touch)` 与 native `WM_NCLBUTTONDOWN` 都写入客户端日志。
- 用途：真机上点边框/拖标题栏后看日志落在哪条路径。若只出现 native 行，说明 web `.win-edge` 热区被 `WM_NCHITTEST` 遮蔽，后续只改 CSS 是无效的；反之亦然。
- **注意：探针只回答"哪条路在生效"，不回答"触摸是否真的失败"——症状本身仍需真机复现。**

### 批次 1 —— 触摸交互

- `duty-agent-ui/src/composables/useHostChrome.ts`：新增纯函数 `chromeDragMessageFor(pointerType)`（touch → `drag-window-touch`，其余 → `drag-window`）。
- `duty-agent-ui/src/components/layout/AppLayout.vue`：拖拽分流接线；`.app-main` 加 `overscroll-behavior: contain`；`@media (any-pointer: coarse)` 下热区（16px/角 28px）与控件（导航 48px、侧栏按钮 44px）加大。窗口三键刻意不动：46px 已 ≥44px，且宿主按 `CONTROLS_WIDTH=138`（3×46）做原生命中区（贴靠按钮飞窗），加宽会让 web 与原生几何错位。
- `duty-agent-ui/src/styles/base.css`：button/a/[role=button] 加 `touch-action: manipulation`（输入框不设，保留正常编辑与粘贴）。
- `DutyAgent.Client/MainForm.cs`：`BeginNativeResizeTouch` 重构为 `BeginNativeTouchModal`——触摸统一走 `WM_SYSCOMMAND + SC_MOVE/SC_SIZE`，且用 `PostMessage` 投递；`ResolveTouchModalCommand` 抽成纯函数供测试。
- `MainForm.cs` 保留 `AreDefaultContextMenusEnabled = true` 并写明原因（触屏长按粘贴）。
- 回归：`DutyAgent.Client.Tests/TouchModalCommandTests.cs`、`duty-agent-ui/src/composables/useHostChrome.test.ts`。
- 命中结构说明：原生 `HitTestNonClient` 已把最外圈 `SM_CXSIZEFRAME + SM_CXPADDEDBORDER`
  （100% DPI 约 8px）判给原生边框，web 热区只覆盖它内侧的带——两条路径合计把触摸的
  有效目标从约 8px 扩到 16px（角 28px）。这正是探针要区分 native / web 的原因：
  若原生路径在触摸下不工作，内侧带也不会兜住最外圈的点按。

### 批次 2 —— 商店防呆

- `scripts/Build-MsixPackage.ps1`：
  - 新增 `-Publisher "CN=<32 位十六进制>"` 参数；提供时覆盖清单 Publisher，格式不合法直接失败。
  - `-ForStore` 下读回 staged manifest 硬校验 Publisher；仍是 `CN=Duty-Agent Dev` 等占位 DN 时抛错，不再只打 Warning。
  - 开发证书缓存目录按发布者 SHA-256 前 16 位派生（`.msix-work/cert/<hash>/`）：换发布者后自动重新生成，避免拿旧 subject 证书签新清单失败。
  - 文件加 UTF-8 BOM：原先无 BOM + 中文注释在 Windows PowerShell 5.1 下按 ANSI 解析，实测会产生语法错误（README 的调用方式正是 `powershell -File`）。
- `DutyAgent.Client/MainForm.cs`：启动时用 `CoreWebView2Environment.GetAvailableBrowserVersionString()` 探测运行时；缺失时抛出带官方下载地址的错误（不随包分发，Fixed Version 会让包体再涨 250MB+）。

### 批次 3 —— 自启与并存

- `DutyAgent.Client/AutoStartManager.cs`：
  - 包身份下 Run 值名改为 `DutyAgent.<PackageFamilyName>`（包身份写 HKCU 不重定向，裸名会与 zip 版互相覆盖）。
  - `IsEnabled()` 兼容旧裸值名（仅当命令确为本包 AUMID 时）；`Apply()` 写入新名后删除旧值，避免同一次登录双启动。
  - `startupTask` 扩展可用时清掉全部各形态 Run 痕迹（否则系统设置禁用后仍会被 Run 键拉起）。
  - 纯函数 `BuildRunValueName` / `IsLegacyPackagedRunCommand` 供回归：`DutyAgent.Client.Tests/AutoStartRunValueTests.cs`。
- `DutyAgent.Client/Program.cs`：新增**不带包前缀**的数据目录 owner 互斥（`Local\DutyAgent.DataDirectory.Owner`）与跨形态唤醒事件。zip 版与商店版可并存安装，但同机只允许一个客户端持有后端；后启动者唤醒既有实例后退出，避免两个后端写坏共享的 `state/roster/meta`。
- `DutyAgent.Client/MainForm.cs`：同时监听同形态唤醒与跨形态唤醒两个事件。

## 本机验证记录（2026-10-17）

工具链：便携安装 .NET SDK 8.0.426（temp 目录，未动系统）+ Node 20.20.2 zip 版。

| 验证 | 命令 | 结果 |
|---|---|---|
| 客户端编译 | `dotnet build DutyAgent.Client.Tests -c Release` | ✅ 0 警告 0 错误 |
| 客户端单测 | `dotnet test DutyAgent.Client.Tests -c Release` | ✅ 32/32（含新增 TouchModalCommandTests、AutoStartRunValueTests） |
| 桥接单测 | `dotnet test DutyAgentBridge.Tests -c Release` | ✅ 9/9 |
| 前端单测 | `npm test`（vitest run） | ✅ 5 文件 29/29（含新增 useHostChrome.test.ts） |
| 前端构建 | `npm run build`（vue-tsc -b + vite build） | ✅ type-check 与产物构建通过 |
| 打包脚本 | PS 5.1 解析 + 发布者正则 + 证书键派生 | ✅ 语法 OK；`CN=<32hex>` 通过、占位 DN 拒绝 |

未能验证（环境/硬件缺失，勿宣称已通过）：

- MSIX 打包 / `-ForStore` 端到端（本机无 winapp 工具链）。
- WACK 认证、商店提交流程。
- 真机触摸行为（批次 0 探针与批次 1 交互手感）与 2-in-1 的 `any-pointer: coarse` 命中。
- 覆盖安装（v1 → v2）与卸载残留检查。

## 明确决策（本轮不做的事）

| 项 | 决策 | 原因 |
|---|---|---|
| manifest MinVersion | **保持 10.0.18362.0 不变** | WebView2 缺失是运行时问题，不是 OS 版本问题；提版本会无谓丢用户。修复=检测 + 引导（已落地） |
| MSIX 自启的 `--silent` 丢失 | **接受并记录为已知差异** | AUMID/StartupTask 均不支持传参；后续可行方案（`IApplicationActivationManager` launcher 或协议激活）需要单独的包内组件与真机验证，不在本轮 |
| 触摸缩放热区的最终修法 | **等探针结果** | 若 native `WM_NCHITTEST` 遮蔽 web 热区，需要在原生侧加宽命中区，而不是继续放大 CSS |
| 包外写入（ClassIsland 插件安装） | 维持现状 + 错误指引 | 属既有功能边界，与商店认证无直接冲突 |
| DPI awareness | 维持 SystemAware | 切 PerMonitorV2 会与现有 `HitTestNonClient` 的坐标假设冲突，需独立评估 |

## 待办：真机验证清单

1. `DUTY_CHROME_PROBE=1`：触摸拖标题栏 / 拖边框，记录日志路径（native vs web）。
2. 触摸拖拽、缩放、长按（应出现粘贴菜单）、双击标题栏最大化。
3. `any-pointer: coarse` 媒体查询在目标 2-in-1 上是否命中（主指针为鼠标时）。
4. DPI 100% / 150% / 200% 下三键与热区命中是否偏移。
5. zip 版 + MSIX 版同机：后启动者应唤醒前者并退出；确认日志与窗口行为。
6. MSIX 首装→卸载→重装：Run 值名/启动项不残留、不双启动。
7. 触屏键盘：在无硬件键盘的设备上确认 API Key 等输入框聚焦时是否自动弹出
   TabTip；若系统不自动弹（Win32 宿主的已知不确定项），需要 `ITipInvocation`
   主动唤起，属独立批次。

## 待办：商店提交侧（非代码）

- [ ] 完整 Windows SDK 下恢复 `desktop6:startupTask` 扩展（批次 4 spike）。
- [ ] WACK（Windows App Certification Kit）跑一遍认证测试。
- [ ] 隐私政策 URL（名单姓名会发往用户配置的 LLM provider）。
- [ ] 商店截图与全尺寸磁贴资产最终确认。
- [ ] v1 → v2 覆盖安装回归（LocalState/LocalAppData 保留、token、桥接 meta、自启自愈）。
- [ ] 提交说明中注明：MSIX 自启显示主窗口（静默参数不可用）；同机运行时只允许一个客户端。
