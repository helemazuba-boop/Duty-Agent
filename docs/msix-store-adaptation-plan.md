# 上架微软应用商店（MSIX）适配计划

## 目标与范围

把 `DutyAgent.Client`（WinForms + WebView2 + 内嵌 Python 后端的独立客户端）从
"zip 分发"改造为可提交 Partner Center 的 MSIX 包，同时**不破坏现有 zip 版用户与
便携版 ClassIsland 用户的可用性**。

桥接插件（`Duty-Agent-ClassIsland-Bridge`）本身运行在 ClassIsland 进程内，**不需要**上架，
但需要为"客户端变成带包身份的进程"这一变化做少量配合改造。

本轮不做：跨架构（x86/arm64）、Store 商业化（内购/许可）、自动更新自建通道。

## 现状调查结论（只读核实）

### 已确认无需改造

| 项 | 依据 |
|---|---|
| 后端只绑 loopback | `Assets_Duty/core.py:328` `host="127.0.0.1"`，无入站监听、无需防火墙例外 |
| 数据与日志路径合法 | 客户端恒传绝对 `--data-dir`（`BackendProcessManager.cs:124-125`），后端 `runtime.py:65` 落 `%LOCALAPPDATA%\DutyAgent\logs` |
| 无自更新逻辑 | 更新交由 Store，无需改动 |
| WebView2 依赖 Evergreen Runtime | Win10 21H2+/Win11 基本预装，不必打包运行时 |
| 无管理员提权需求 | 全流程用户态 |

### 确认需要改造

| # | 问题 | 证据 | 风险等级 |
|---|---|---|---|
| A | 握手依赖写入**包外任意路径** | 客户端把 `.duty-agent-meta.json` 写进 `<ClassIsland 配置目录>\DutyAgentBridge\`（`BackendProcessManager.cs:326-328`），而配置目录常由"运行中 ClassIsland 进程目录"推导出 `D:\ClassIsland_app_windows_x64_full_folder\data\Config`（`ClassIslandConfigLocator.cs:27-33`） | **高**：Store 需 `broadFileSystemAccess` 类宽泛能力，审核要单独说明，且违反 Store "不写外部路径" 的推荐做法 |
| B | 无 MSIX 打包工程 | 唯一产物是 zip（`New-DutyAgentClientRelease.ps1:530`），仓库无 `.wapproj` / `.appxmanifest` | **高**：从零开始 |
| C | 自启用 `HKCU\...\Run` | `AutoStartManager.cs:11` | 中：MSIX 下写入不进包私有视图则卸载后残留指向已删 exe 的 Run 值；Store 推荐 `StartupTask` |
| D | 手写开始菜单 `.lnk` + 硬编码 AUMID | `WindowsShortcutActivator.cs:20,38`、`DesktopNotificationService.cs:74` | 中：Store 自管磁贴；AUMID 不匹配则 toast 静默失败 |
| E | Python 子进程 CWD 在只读安装目录 | `BackendProcessManager.cs:95` + 发布脚本删除全部 `.pyc`（`New-DutyAgentClientRelease.ps1:399-402`） | 中：运行期写 `__pycache__` 到只读目录会刷 `Access denied` |
| F | 单实例命名裸名 | `Program.cs:9-10` `Local\DutyAgent.Client.SingleInstance` | 中：同机 zip 版与 Store 版互相当成"已有实例"；也可能双后端抢端口 |
| G | WebView2 userDataFolder 在 LocalAppData 裸路径 | `MainForm.cs:600-603` | 低：可卸载后残留，非阻断 |

## 分批方案

### 批次 1 —— 握手目录收敛（A）

**思路：双写 + 双读，而不是单点迁移。** 单边改会造成"新客户端 + 旧桥接"或"旧客户端 +
新桥接"必然瞎一边；双写双读可以让任意组合都工作，迁移零风险。

客户端侧：
- `BackendProcessManager` 增加 `AlternateBridgeMetaPath`（固定 `%LOCALAPPDATA%\ClassIsland\Config\DutyAgentBridge\.duty-agent-meta.json`）
  与 `BridgeMetaPaths`（去重后的完整候选列表）。
- `WriteBridgeMetaFile()` 改为按候选列表逐个写入：**至少一处成功即算成功**，
  单处失败只记 Warn 不抛（宿主布局目录在包身份下可能不可写）。
  写入后**逐个读回校验**（pid/port 一致），并把每个路径实际解析到的完整物理位置
  打进客户端 INFO 日志 —— 这是商店版上线后唯一能证明"桥接看到的是同一个目录"的对账依据。
- `DeleteBridgeMetaFile()` 同样按候选列表清理，保留 `MetaFileBelongsToCurrentProcess` 归属校验。

桥接侧：
- `IBridgePaths` / `BridgePaths` 增加 `AlternateMetaFilePath`、`MetaFilePaths`、
  以及兜底层 `FallbackMetaFilePaths`（扫描 `%LOCALAPPDATA%\Packages\<任意包>\{LocalCache\Local,LocalState}\
  ClassIsland\Config\DutyAgentBridge`）。
- `IpcBridgeService.ReloadMeta()`：主层 → 兜底层，主层有结果就不碰兜底层。
- `HealthMonitorService`：`FileSystemWatcher` 同时监听两处目录，`CheckMetaFile()` 从全部候选中
  选**最应该连的那一个**（见下方修正）。

**验收**：制造"只写备路径"场景（把宿主布局目录置为只读或临时改指向），桥接仍能连接；
反向（只写主路径）行为与现在完全一致。回归覆盖见 `Duty-Agent-ClassIsland-Bridge.Tests`。

### 批次 1 修正 A：选取规则不能是"第一个存在的"

> 落地后复盘发现的缺陷，已连同下方修正 B 一起修复并有测试钉住。

初版实现是"遍历候选路径，取第一个文件存在且进程活着的"。这在多写方案下不成立：
旧路径完全可能留着**昨天**的握手文件。两种都满足"文件存在"：
1. 昨天那次非正常退出没删掉（进程已死）；
2. 昨天那个客户端还在跑 —— zip 版与商店版同机并存时**真的会发生**。

`ReloadMeta` 当时还有 `continue` 兜底，但 `HealthMonitorService.CheckMetaFile()` /
`ComputeMetaSignature()` 用的是 `FirstOrDefault(File.Exists)`，**完全没有有效性检查**，
会直接拿旧文件的 mtime/pid 去驱动连接决策，把桥接引向一个早已失效的端口。

**修正后的规则**（收敛在 `BridgeMetaResolver`，一处定义三处复用，避免再漂移）：
1. **有效性**：文件存在 → 能解析 → `pid > 0` → 该 pid 活着**且进程名是 `duty-agent`**
   （顺带修掉更隐蔽的一个 bug：PID 复用时 `GetProcessById(pid)` 会返回"活着"的假象，
   `IsProcessAlive` 会把陈旧 meta 当成有效实例）；
2. **新鲜度**：全部有效候选中取 `started_at` 最新的；相同取文件 mtime 更新的；
   再相同取候选列表更靠前的（更"正统"的路径）；
3. 返回"最应该连的那一个"，而不是"最先看到的那一个"。

同时删除了 `HealthMonitorService.IsProcessAlive` 与 `IpcBridgeService.IsProcessRunning`
两个只被旧逻辑使用的私有方法 —— 留着就会成为第二个真相源。

### 批次 1 修正 B：MSIX 的 AppData 隔离 —— 已实证（2026-10-13）

> 原文的顾虑是"客户端和桥接进程是否共享同一个实际目录，需要实测，而不是因为路径字符串
> 一样就认为没问题"。**已完成实测**：用 `winapp create-debug-identity` 给客户端 exe 挂
> 稀疏包身份，经 `shell:AppsFolder\<PFN>!App` 激活真实运行，再把读到的物理落点与客户端
> INFO 日志对账。

**实测结论（均有日志 / 文件系统 / 注册表证据）**：

| # | 问题 | 实测结果 |
|---|---|---|
| 1 | 包身份下客户端能否完整工作 | 能。消息循环、主窗口、ClassIsland 配置目录发现、`桥接 meta 已写入 3 个路径`、通知流连接、Python 后端启动/监管/异常退出自动重启，全部正常 |
| 2 | meta 三处落点是否一致、桥接（非包进程）能否读到 | 一致且可读。`%LOCALAPPDATA%\ClassIsland\Config\...`、宿主布局目录、`Packages\<PFN>\LocalState\ClassIsland\Config\...` 三份 md5 相同；从 bash（非包进程）可直接读到 Win32 路径那份 —— **包身份进程写 Win32 `%LOCALAPPDATA%` 子路径未发生重定向** |
| 3 | `ApplicationData.Current.LocalFolder` 指向 | `C:\Users\...\AppData\Local\Packages\DutyAgent_3ecm5aq137n80\LocalState`（客户端 INFO 日志原样打出） |
| 4 | HKCU Run 键写入是否重定向 | **不重定向**。包身份进程写 `HKCU\...\Run` 后，非包进程（reg query / C# winreg）立刻读到，登录时的 explorer 同样读得到 |
| 5 | AUMID 激活能否拉起全信任客户端 | 能。`shell:AppsFolder\DutyAgent_3ecm5aq137n80!App` 启动成功；把 Run 键里的 `explorer.exe shell:AppsFolder\<PFN>!App` 原样执行也启动成功 —— 自启回退链路的最后一环成立 |

冗余策略保留不变（三写 + 桥接三处能发现），因为它同时覆盖"宿主布局目录不可写"的降级
场景；但语义疑云已消除：**Win32 LocalAppData 路径就是桥接与客户端共享的真实目录**。

### 批次 1 修正 B 附带发现：本机 MSIX 工具链无法校验任何 desktop 扩展

同一轮实测暴露了一个工具链硬限制，它直接改写了自启方案（见批次 3 修正）：

- 本机三套 makeappx —— NuGet `Microsoft.Windows.SDK.BuildTools`（10.0.28000 /
  10.0.26100 / 10.0.22621 三个版本都验证过）与 `winapp 0.7.1` 内置的那个 —— 校验清单用的
  schema 集（`schemas/10.0.*/winrt/*.xsd`，22 个文件）**只有 Foundation +
  UapManifestSchema，没有任何 Desktop\* schema**。
- 直接后果：清单里出现任何 `desktop:` / `desktop6:` 扩展元素都会被判
  `error C00CE014 / C00CE169`（`{foundation}Extensions` 不允许该子元素 / `Category`
  不在枚举内）。对照组 `uap:Extension` 可以正常通过 —— 证明不是校验器整体失效，
  就是 desktop schema 缺失。该现象与 `IgnorableNamespaces` 是否列了 `desktop` 无关。
- 因此**本机（及任何只装 NuGet BuildTools 的 CI）不能产出或验证带 desktop 扩展的清单**；
  这不是我们的清单写错了 —— 同样的元素在完整 Windows SDK 下合法且商店常见。

### 批次 2 —— MSIX 打包工程（B）

- 新增 `DutyAgent.Client.Package/`：
  - `Package.appxmanifest`：包身份占位、`runFullTrust`、`uap:VisualElements`（托盘/磁贴）、
    最小版本 10.0.17763.0。**不再声明任何 `<Extensions>`**，原因与"完整 Windows SDK
    下的恢复方法"都写在清单内注释里。
  - `Assets/`：由 `icon.png` 经 `winapp manifest update-assets` 生成的 49 张商店资产
    （MedTile / AppList / WideTile / StoreLogo 全尺寸）。
  - ~~`DutyAgent.Client.Package.wapproj`~~ **已删除**：Desktop Bridge MSBuild 目标需要
    Visual Studio，本机（及纯 .NET SDK 环境）不可用；winapp CLI 代替它完成打包。
  - `README.md`：发布者 ID 替换说明、构建与签名命令、商店提交流程。
- `scripts/Build-MsixPackage.ps1` 重写为 winapp 流程：发布客户端 → 组装 winapp 布局 →
  `winapp package`。构建机只要有 .NET SDK + `@microsoft/winappcli`（npm），
  **不需要 Visual Studio 或 Windows Kits**。
- **本机已实证产出 .msix**（2026-10-13）：`DutyAgent_1.0.0.0_x64.msix`，107 MB，开发证书
  签名；解包校验通过 —— `AppxManifest.xml`（无 Extensions）、49 资产、`Assets_Duty`
  1707 个文件（`python-embed` 完整：`python311.dll` 5.8M + 全部 `.pyd` + `Lib/` +
  `web/index.html`）、随包桥接 `DutyAgentBridge.dll` + `manifest.yml`、`resources.pri`。
- **打包性能坑**：`winapp package` 先把整个布局复制到 `%TEMP%` 再压缩。C: 剩余空间不足时
  会慢到看似假死（实测 19 分钟未完成）。把 `TMP`/`TEMP` 指到空闲盘后，同样 261 MB
  只用 **19 秒**。

### 批次 3 —— 自启 / 快捷方式 / 通知适配包身份（C、D）

- `AutoStartManager`：包身份下优先 `Windows.ApplicationModel.StartupTask`；**清单未声明
  `desktop6:startupTask` 扩展时 `StartupTask.GetAsync` 抛异常**，此时回退 HKCU Run 键，
  命令为 `explorer.exe shell:AppsFolder\<PFN>!App`（AUMID 取
  `WindowsShortcutActivator.AppUserModelId`）。两个前提都已实测：包身份进程写 Run 键不
  重定向（修正 B 表 #4）、AUMID 命令能拉起客户端（表 #5）。`IsEnabled()` 按当前形态分别
  比对两种命令形态，安装形态切换后能自愈改写。回归见 `DutyAgent.Client.Tests`
  （`AutoStartCommandTests`）。
- `WindowsShortcutActivator`：包身份下跳过自建 `.lnk`，`AppUserModelId` 返回包身份 AppId
  (`<PackageFamilyName>!App`)；非包身份行为不变。
- `DesktopNotificationService`：包身份下不再指定 AUMID，让 toast 走当前包身份。

### 批次 4 —— Python 运行时与并发命名加固（E、F、G）

- `BackendProcessManager`：子进程环境加入 `PYTHONDONTWRITEBYTECODE=1`，杜绝往只读安装目录写缓存。
- `Program.cs`：互斥体与唤醒事件名在包身份下加 `<PackageFamilyName>\` 前缀，
  使 Store 版与 zip 版可以并存**安装**（同机只允许一个客户端运行，见修正 C 的
  跨形态数据目录 owner 互斥）。**实测中抓到并修掉一个自身缺陷**：初版把包名前缀拼到了
  `Local\` 之前，Win32 把名字当文件路径，商店版客户端启动即抛
  `DirectoryNotFoundException` 崩溃（minidump 0xE0434352，日志只有第一行）。修正为
  "根前缀必须留在最前"，回归见 `DutyAgent.Client.Tests`（`ScopeNameTests`，8 例，
  含变异测试：把前缀改回错误位置后 6/8 立刻失败）。
- `MainForm`：WebView2 `userDataFolder` 在包身份下改用 `Windows.Storage.ApplicationData.Current.LocalFolder`。
- `PackageIdentityHelper` / `ClientLog`：启动即把 `包身份/PFN/LocalAppData/LocalState`
  打进 INFO 日志，AppDomain 级未处理异常也落日志 —— 商店版排障的最低要求。

### 批次 5 —— 文档

- `README.md` 增补「上架微软商店」章节：前置条件、构建命令、发布者配置、已知限制。

## Store 提交侧待办（非代码）

1. Partner Center 账号（个人开发者账号即可）。
2. 保留产品名与预留应用名称。
3. 隐私政策 URL —— 应用会把名单姓名发往用户配置的 LLM API，条款需覆盖。
4. 年龄分级；`runFullTrust` 能力说明（商店允许，自 Win10 1903 起）。
5. 认证周期约 1–3 天；需在有 Desktop Bridge 工作负载的机器上产出首包。
6. 包体：源 92M（其中 85M 为 python-embed），MSIX 压缩后预计 40M 以内。

## 风险

| 风险 | 应对 |
|---|---|
| ~~本机无 MSIX 工具链，打包工程无法验证~~ **已消除** | 2026-10-13 用 winapp 0.7.1 在本机产出并解包验证了 .msix，包内容/清单/签名全部核对 |
| 带 desktop 扩展的清单在本机工具链无法校验 | 放弃 `startupTask` 扩展改用运行期 Run 键回退（已实测全链路）；清单注释保留完整 SDK 下的恢复 XML |
| 包身份下写入宿主布局目录可能失败 | 批次 1 的双写机制已把这条路径降级为"尽力而为" |
| 商店版与 zip 版同机并存 | 单实例命名加 PFN 作用域（批次 4，已修掉并回归）；握手 meta 三写双读 + 有效性/新鲜度选取（批次 1 修正 A）；**运行期由跨形态数据目录 owner 互斥禁止双客户端（修正 C，2026-10-17）** |
| 桥接插件一键安装仍写 `<...>\data\Plugins` | 保持现状 + 失败时给出明确的手动安装指引；不在本轮改造 |

## 提交节奏

每个批次一个提交，可独立回退：

1. `feat(client): 握手目录双写 + 桥接 meta 双读`（含修正 A / B）
2. `feat(client): 新增 MSIX 打包工程`
3. `feat(client): 包身份下的自启/快捷方式/通知适配`
4. `fix(client): Python 子进程只读目录与并发命名加固`
5. `docs: 上架微软商店适配说明`
6. `feat(client): 包身份实测落地 —— winapp 打包、Run 键自启回退、ScopeName 崩溃修复`

> 批次 1 的修正 A/B 与 `Duty-Agent-ClassIsland-Bridge.Tests`（9 个用例，覆盖
> "旧路径残留"、"PID 复用"、"主层优先"等）在同一批追加提交。
>
> 批次 6 是实测批：修正 B 的五项实测、工具链 desktop schema 缺失的发现、清单去掉
> `<Extensions>`、`AutoStartManager` 的 Run 键回退（`ScopeName` 前缀崩溃的修复 +
> `DutyAgent.Client.Tests` 11 例）、`.wapproj` 删除、`Build-MsixPackage.ps1` 重写为
> winapp 流程、本机 107MB .msix 产出与解包校验，全部在同一批。

## 修正 C（2026-10-17）—— 上架防呆与触摸适配

完整计划、落地记录与真机待办见 `docs/store-touch-remediation-plan.md`，这里只记与本文档
结论相关的要点：

1. **发布者防呆**：`Build-MsixPackage.ps1` 新增 `-Publisher "CN=<32 位十六进制>"`，
   `-ForStore` 读回 staged manifest 硬校验；开发占位 DN 直接构建失败。本地签名证书
   缓存改为按发布者 SHA-256 派生目录（换发布者自动重新生成）。
2. **清单 MinVersion 保持 10.0.18362.0**：官方文档确认 WebView2 在 Win10 上
   "绝大多数预装但仍有缺失"，属运行期问题而非 OS 版本问题。客户端改为启动时探测
   `CoreWebView2Environment.GetAvailableBrowserVersionString()`，缺失时给出官方
   下载指引。
3. **HKCU Run 值名加 PFN 后缀**（修正 B 实测"包身份写 HKCU 不重定向"的直接推论）：
   裸值名会被 zip 版与商店版互相覆盖/双启动；`IsEnabled`/`Apply` 兼容并迁移旧值，
   `startupTask` 可用时清掉全部 Run 痕迹。
4. **跨形态数据目录 owner 互斥**：`Local\DutyAgent.DataDirectory.Owner`（刻意不带
   包前缀）保证 zip 版与商店版运行时只有一个后端写共享的 `state/roster/meta`；
   后启动者通过 `Local\DutyAgent.DataDirectory.Wake` 唤醒既有实例后退出。
5. **脚本编码**：`Build-MsixPackage.ps1` 无 BOM + 中文注释，在 Windows PowerShell 5.1
   下按 ANSI 解析会产生**语法错误**（README 的调用方式正是 `powershell -File`）。
   已加 UTF-8 BOM。README 里的脚本名拼写错误（`Build-MsilPackage.ps1`）一并修正。
6. **触摸适配**（详见新文档批次 0/1）：触摸拖拽/缩放改走
   `WM_SYSCOMMAND + SC_MOVE/SC_SIZE` 且用 `PostMessage` 投递；`DUTY_CHROME_PROBE=1`
   探针用于真机判定 native / web 命中路径；`any-pointer: coarse` 下放大热区与控件。
   默认上下文菜单**保留**（触屏长按粘贴入口）。
