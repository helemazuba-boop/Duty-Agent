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
- `DeleteBridgeMetaFile()` 同样按候选列表清理，保留 `MetaFileBelongsToCurrentProcess` 归属校验。

桥接侧：
- `IBridgePaths` / `BridgePaths` 增加 `AlternateMetaFilePath`。
- `IpcBridgeService.ReloadMeta()`：主路径不存在时回退备路径。
- `HealthMonitorService`：`FileSystemWatcher` 同时监听两处目录，`CheckMetaFile()` 按候选列表
  取第一个存在的文件（主路径优先）。

**验收**：制造"只写备路径"场景（把宿主布局目录置为只读或临时改指向），桥接仍能连接；
反向（只写主路径）行为与现在完全一致。

### 批次 2 —— MSIX 打包工程（B）

- 新增 `DutyAgent.Client.Package/`：
  - `Package.appxmanifest`：包身份占位、`runFullTrust`、`windows.startupTask` 扩展、
    `uap:VisualElements`（托盘/磁贴）、最小版本 10.0.17763.0。
  - `DutyAgent.Client.Package.wapproj`：Windows Application Packaging Project，
    引用发布产物（`dotnet publish` 输出的 client 目录），包含 `Assets_Duty` 全部资产。
  - `README.md`：发布者 ID 替换说明、构建与签名命令、商店提交流程。
- 新增 `scripts/Build-MsixPackage.ps1`：一条命令跑完「发布客户端 → 组装 msix」。
- **本地无法产出 .msix**：本机没有 Windows Kits / MakeAppx / DesktopBridge MSBuild 目标
  （`.NET` SDK 只从 NuGet 拉 Windows 契约）。此项须在装有 "Windows SDK + Desktop Bridge"
  工作负载的机器上验证，见「风险」。

### 批次 3 —— 自启 / 快捷方式 / 通知适配包身份（C、D）

- `AutoStartManager`：`PackageIdentity` 可用时改用 `Windows.ApplicationModel.StartupTask`
  （`net8.0-windows10.0.17763.0` 下 WinRT 可直接调用，已实测编译通过）；
  非包身份时保持原 `HKCU Run` 行为不变。
- `WindowsShortcutActivator`：包身份下跳过自建 `.lnk`，`AppUserModelId` 返回包身份 AppId
  (`<PackageFamilyName>!App`)；非包身份行为不变。
- `DesktopNotificationService`：包身份下不再指定 AUMID，让 toast 走当前包身份。

### 批次 4 —— Python 运行时与并发命名加固（E、F、G）

- `BackendProcessManager`：子进程环境加入 `PYTHONDONTWRITEBYTECODE=1`，杜绝往只读安装目录写缓存。
- `Program.cs`：互斥体与唤醒事件名在包身份下加 `<PackageFamilyName>\` 前缀，
  使 Store 版与 zip 版可以并存。
- `MainForm`：WebView2 `userDataFolder` 在包身份下改用 `Windows.Storage.ApplicationData.Current.LocalFolder`。

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
| 本机无 MSIX 工具链，打包工程无法验证 | 批次 2 交付"可审阅的工程 + 明确验证步骤"，不假装已验证；首包在目标机器产出 |
| 包身份下写入宿主布局目录可能失败 | 批次 1 的双写机制已把这条路径降级为"尽力而为" |
| .NET 8 + WinForms + self-contained 的 WAP 组合有已知摩擦 | 批次 3 先只做代码层改造（与打包工程解耦），降低 spike 失败的牵连面 |
| 桥接插件一键安装仍写 `<...>\data\Plugins` | 保持现状 + 失败时给出明确的手动安装指引；不在本轮改造 |

## 提交节奏

每个批次一个提交，可独立回退：

1. `feat(client): 握手目录双写 + 桥接 meta 双读`
2. `feat(client): 新增 MSIX 打包工程`
3. `feat(client): 包身份下的自启/快捷方式/通知适配`
4. `fix(client): Python 子进程只读目录与并发命名加固`
5. `docs: 上架微软商店适配说明`
