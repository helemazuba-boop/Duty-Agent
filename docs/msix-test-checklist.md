# MSIX 异机测试清单（侧载 / 小范围）

目标：在一台**没装过本项目**的 Windows 10/11 设备上，验证 MSIX 包能否装、能否跑、
行为是否与开发机一致。

这份文档随 `scripts\Build-MsixPackage.ps1 -PrepareTestBundle`（默认开）复制进
`.msix-work\test-bundle\`，和产物一起发走。

---

## 0. 测试包里有什么

| 文件 | 说明 |
|---|---|
| `<Name>_<Version>_x64.msix` | 安装包（约 109 MB，已用开发证书签名） |
| `devcert.cer` | 开发证书**公钥**（subject 必须等于清单 `Identity.Publisher`） |
| `build-info.json` | 本次构建的 Name / Publisher / Version / 证书 subject 与指纹 / 构建时间 |
| `Install-MsixForTesting.ps1` | 导入证书 + 安装（需管理员） |
| `Uninstall-MsixForTesting.ps1` | 卸载 + 清理自启残留（需管理员） |
| `bridge\` | ClassIsland 桥接插件（9 个文件，约 750 KB）。**只有**要验桥接联动时才用，见第 4 节 |
| `msix-test-checklist.md` | 本文件 |

> **不要**把 `devcert.pfx`（带私钥，口令是 winapp 默认的 `password`）发给任何人。
> 侧载只需要 `.cer`。

## 前置条件

| 项 | 要求 | 怎么查 |
|---|---|---|
| 系统版本 | **Windows 10 build 18362（1903）或更高**，x64 | `winver` |
| 架构 | **x64**（本包不含 arm64 / x86） | 设置 → 系统 → 关于 |
| 管理员权限 | 装证书和装包都要 | — |
| 磁盘空闲 | ≥ 400 MB（安装后占用约 300 MB） | — |
| **WebView2 Runtime** | **建议先装**，否则主界面打不开 | 见下 |
| ClassIsland | **只有**要验证「桥接联动」时才需要 | — |

### WebView2 Runtime

主界面是 WebView2 承载的网页。Windows 11 自带，**Windows 10 通常没有**。

- 检测：注册表 `HKLM\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}`
  有 `pv` 值即为已装（安装脚本会自动检测并提示）。
- 安装：<https://go.microsoft.com/fwlink/p/?LinkId=2124703>

> 缺 WebView2 **不会崩溃**：客户端弹「启动失败 / 请确认系统已安装 WebView2 Runtime」，
> 托盘与后端照常运行。这是预期行为。

---

## 1. 安装

1. 把整个测试包目录拷到目标机（U 盘 / 局域网均可）。
2. 管理员 PowerShell：
   ```powershell
   Set-ExecutionPolicy -Scope Process Bypass -Force
   .\Install-MsixForTesting.ps1
   ```
3. **期望输出**：系统版本 / WebView2 状态 → 证书 subject 与清单 Publisher 一致 → 证书导入 →
   `Add-AppxPackage` 成功 → 末尾打印 `Name` / `Version` / **PFN** / AUMID。

> 安装脚本会**先把包内 `AppxManifest.xml` 读出来**，校验证书 subject 是否等于
> `Identity.Publisher`。不一致会直接报错——这比 `Add-AppxPackage` 吐一个
> `0x800B0109` 好排查得多。

### 常见报错对照

| 报错 | 含义 | 处理 |
|---|---|---|
| `0x800B0109` / 证书链不受信任 | 证书没进「受信任人」 | 用**管理员**重跑；确认 `devcert.cer` 与包是同一批产物 |
| `0x80073CFF` | 系统策略禁止旁加载 | 设置 → 更新和安全 → 开发者选项 → 打开「旁加载应用」或「开发人员模式」 |
| `0x80073CFD` / 提示需要更高版本 | 系统低于 1903 | 换机器 |
| `0x80073CF9` | 已装同版本包 | 先跑 `Uninstall-MsixForTesting.ps1`；脚本检测到同版本时会自动加 `-ForceUpdateFromAnyVersion` |
| `0x80073CF3` | 依赖缺失 | 把完整报错发回来 |

---

## 2. 启动与基本可用性

开始菜单搜索「Duty-Agent」启动（或 `explorer.exe shell:AppsFolder\<PFN>!App`）。

- [ ] **主窗口出现**，WebView 里加载出完整界面（不是白屏、不是「启动失败」弹窗）
- [ ] **托盘图标**出现在任务栏右下角
- [ ] 窗口能拖拽移动、能缩放（自绘铬 + app-region 生效）
- [ ] 再点一次开始菜单里的图标 → **不会开出第二个窗口**（单实例生效）

---

## 3. 关键实测项

### 3.1 包身份识别

打开 `%LOCALAPPDATA%\DutyAgent\data\logs\client-<日期>.log`，**第一行**应为：

```
[INFO] 客户端启动: 包身份=是(MSIX), PFN=<PFN>, LocalAppData=..., LocalState=..., 参数=[...]
```

- [ ] `包身份=是(MSIX)` 且 `PFN` 与安装脚本打印的一致

> 这一行是历史上出过崩溃的地方（`ScopeName` 前缀顺序错误导致包身份下启动即崩）。

### 3.2 后端拉起与监管

同一份日志里应能看到：

```
[INFO] 桥接 meta 已写入 N 个路径：<绝对路径列表>
[INFO] 通知流已连接。
```

- [ ] 出现上述两行
- [ ] 任务管理器里能看到 `duty-agent.exe` 与一个 `python.exe`（路径在 `...\WindowsApps\...` 下）

### 3.3 数据落点是否被重定向（核心项）

在文件资源管理器地址栏分别打开：

- `%LOCALAPPDATA%\DutyAgent\data\logs`
- `%LOCALAPPDATA%\Packages\<PFN>\LocalState`

- [ ] 日志实际落在 **`%LOCALAPPDATA%\DutyAgent\data\logs`**（未被重定向）
- [ ] `...\Packages\...\LocalState` 下**只有** WebView2 的缓存目录，没有排班数据

> 开发机在**稀疏包身份**（debug identity）下实测「不重定向」。完整 MSIX 安装是否同样
> 如此，正是这条要坐实的。若结论相反（数据跑进 `LocalState`），说明 zip 版和商店版会
> **各存一份数据**，是设计问题 —— 请务必回报。

### 3.4 自启

包身份下 Run 值名带 PFN 作用域：**`DutyAgent.<PFN>`**（不是裸 `DutyAgent`，
那是 zip 版用的，两者刻意分开避免互相覆盖）。

```powershell
Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' |
    Select-Object -Property DutyAgent* | Format-List
```

- [ ] 存在名为 `DutyAgent.<PFN>` 的值，内容为 `explorer.exe shell:AppsFolder\<PFN>!App`
- [ ] 界面里关闭「开机自启」后该值消失
- [ ] 同机 zip 版写的裸 `DutyAgent`（exe 路径）**不受影响**

（清单没声明 `desktop6:startupTask` 扩展，所以走 Run 键回退；系统「启动应用」里看不到它是正常的。
另：AUMID 激活无法传 `--silent`，登录后会显示主窗口而不是静默驻留托盘，属已知差异。）

---

## 4. 可选：桥接联动（需要 ClassIsland）

> 客户端包内自带一份桥接（`<安装目录>\bridge\`），客户端界面的
> **"安装 ClassIsland 插件"** 就是把它拷进 `<ClassIsland>\data\Plugins\duty-agent-bridge\`。
> 先试按钮；按钮报"未找到随包的桥接插件目录"或"无法定位 ClassIsland 插件目录"时，
> 用手工方式。

### 4.1 安装

**方式 A（推荐）**：客户端界面点"安装 ClassIsland 插件" → 重启 ClassIsland。

**方式 B（手工）**：把测试包里的 `bridge\` 整个目录拷过去：

```powershell
# ClassIsland 便携版就是它的 data 目录；装版通常在 %LOCALAPPDATA%\ClassIsland
$target = "<ClassIsland>\data\Plugins\duty-agent-bridge"
New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item -Path ".\bridge\*" -Destination $target -Recurse -Force
```

拷完重启 ClassIsland，插件列表里应出现「Duty-Agent 桥接」。

> ⚠️ **别用** `Duty-Agent-ClassIsland-Bridge\cipx\DutyAgentBridge.cipx`——那是
> 2026-09-12 的旧构建（DLL 136 KB，当前 166 KB），装上会是落后多代的插件。

### 4.2 确认加载的是这一份

插件 `manifest.yml` 里 version 一直是 `0.50.0`，光看版本号分不出新旧。**比哈希**：

```powershell
(Get-FileHash "<ClassIsland>\data\Plugins\duty-agent-bridge\DutyAgentBridge.dll" -Algorithm MD5).Hash
(Get-FileHash ".\bridge\DutyAgentBridge.dll" -Algorithm MD5).Hash   # 应与上面一致
```

### 4.3 验收项

- [ ] ClassIsland 插件列表里能看到「Duty-Agent 桥接」，且无加载错误
- [ ] 插件能识别到 Duty-Agent 在线（状态不是"未连接"）
- [ ] 触发一次排班，ClassIsland 侧收到通知（`schedule_completed`）
- [ ] 客户端与 ClassIsland 读到的 `.duty-agent-meta.json` 是**同一个文件**
      （比对 mtime / MD5）——这条是包身份下最容易出问题的：客户端在包内跑，
      桥接在 ClassIsland 进程里跑，两者必须落到同一个真实路径
- [ ] 若装了桌面组件「值日人员」，显示的人员与客户端一致

> 客户端启动时会打印 `[INFO] 桥接 meta 已写入 N 个路径：...`（这是客户端的日志）。
> 把那段路径贴进回报里——桥接侧目前**不会**在界面或日志里回显它读的是哪个文件，
> 出问题时只能靠这两边的路径对不上来定位。桥接自己的日志在
> `<ClassIsland>\data\Config\Plugins\duty-agent-bridge\logs\`。

---

## 5. 卸载

```powershell
.\Uninstall-MsixForTesting.ps1
```

- [ ] `Get-AppxPackage -Name HelemaZuba.Duty-Agent` 查不到
- [ ] `DutyAgent.<PFN>` 已消失（脚本按 PFN 作用域精确清理）
- [ ] 同机 zip 版的裸 `DutyAgent` 值**仍在**（脚本不会误删）
- [ ] `%LOCALAPPDATA%\DutyAgent` 仍在（设计如此：卸载不删用户数据）

> 开发证书默认**保留**在「受信任人」里，方便反复安装。要一并清掉加 `-RemoveCertificate`。

---

## 6. 已知限制（不用当 bug 报）

- **仅 x64**，不支持 arm64 / x86。
- **zip 版与商店版可以同机安装**：数据目录共用 `%LOCALAPPDATA%\DutyAgent`，但有一把
  不带包前缀的数据目录 owner 互斥（`Local\DutyAgent.DataDirectory.Owner`）——同机只允许
  一个客户端持有后端，后启动的一方会唤醒既有实例后退出。**所以不要期待两个都在跑。**
- 首次启动要解包 1700+ 个文件，比平时慢，属正常。
- 未做 Store 商业化（内购 / 许可），未接 Store 增量更新通道。
- 图标资源是脚本批量生成的，视觉细节可能还需人工过一遍。

---

## 7. 请回报什么

出现任何不符，把下面三样发回来基本能一次定位：

1. 安装 / 卸载脚本的**完整输出**；
2. `%LOCALAPPDATA%\DutyAgent\data\logs\` 下**最新那份** `client-*.log`；
3. 报错时的**截图**（尤其「启动失败」对话框）。

重点看 **3.3 数据落点** 和 **3.1 包身份行**。
