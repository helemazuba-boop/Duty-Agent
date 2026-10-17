# DutyAgent.Client MSIX 打包工程

把独立客户端打成可提交微软商店（Partner Center）的 MSIX 包。

> ✅ **本机已实证**（2026-10-13）：用 `@microsoft/winappcli`（winapp 0.7.1）在本机产出并
> 解包验证了 `.msix`。**不需要 Visual Studio、不需要 Windows Kits**，构建机只要有
> .NET SDK + Node/npm 即可。实测数据见 `docs/msix-store-adaptation-plan.md` 修正 B。

## 构建

```powershell
# 一条命令：发布客户端 → 组装 winapp 布局 → winapp package → 开发证书签名
powershell -ExecutionPolicy Bypass -File scripts\Build-MsixPackage.ps1

# 指定版本（同步盖 version.py / duty-agent.exe / 桥接 manifest，与发布脚本一致）
powershell -ExecutionPolicy Bypass -File scripts\Build-MsixPackage.ps1 -ReleaseVersion 1.2.3

# 商店提交模式：不本地签名（交给 Partner Center），发布者 ID 必填
powershell -ExecutionPolicy Bypass -File scripts\Build-MsixPackage.ps1 -ReleaseVersion 1.2.3 -ForStore -Publisher "CN=<32 位十六进制发布者 ID>"
```

产物在 `.msix-work\`：`DutyAgent_<版本>_x64.msix`（开发证书签名）或同名未签名包（`-ForStore`）。
布局暂存目录为 `.msix-work\pkg`，证书按发布者 DN 派生缓存于 `.msix-work\cert\<发布者哈希>\devcert.pfx`
（换 `-Publisher`/清单发布者后自动重新生成，避免旧 subject 证书签名失败）。

> 性能提示：`winapp package` 会先把约 260MB 的布局复制到 `%TEMP%` 再压缩。
> C: 剩余空间不足时该阶段会慢到看似假死（实测 19 分钟未完成）；脚本已把
> `TMP`/`TEMP` 指到 `.msix-work\temp`，同体积实测约 20 秒。

## 提交商店前必须做的事

1. **替换发布者标识**。用 `-Publisher "CN=<32 位十六进制>"` 传入 Partner Center →
   账户设置里给出的发布者 ID（构建时覆盖清单里的 `CN=Duty-Agent Dev` 开发 DN），
   并以 `-ForStore`（`--no-sign`）交付由商店签名。`-ForStore` 会**硬校验**最终
   Publisher 必须是商店 ID，占位 DN 直接构建失败而不是只打警告。
2. **检查商店图标资产**。`Assets\*.png` 已由 `winapp manifest update-assets <仓库根>\icon.png`
   生成 49 张（MedTile / AppList / WideTile / StoreLogo 全尺寸），已入库；
   换图标后重跑该命令即可，无需手工缩放。
3. **年龄分级与隐私政策 URL**。应用会把名单姓名发往用户配置的 LLM API，隐私条款必须覆盖。
4. 商店侧提交 `.msix`（或打成 `.msixupload`）；认证通常 1–3 天。

## 本地侧载 / 包身份测试

```powershell
# 完整安装（开发证书需先被信任，Build-MsixPackage.ps1 已尝试自动安装）
Add-AppxPackage -Path .msix-work\DutyAgent_1.0.0.0_x64.msix
# 卸载
Get-AppxPackage DutyAgent | Remove-AppxPackage
```

免安装验证包身份相关行为（本轮实测方案）：

```powershell
winapp create-debug-identity .msix-work\pkg\duty-agent.exe --manifest .msix-work\pkg\Package.appxmanifest --keep-identity
# 经 AUMID 激活：shell:AppsFolder\<PackageFamilyName>!App
# 清理：winapp unregister --manifest .msix-work\pkg\Package.appxmanifest --force
```

## 与 zip 版的关系

- 两种形态可以同机安装：单实例互斥体名与唤醒事件名在包身份下会加 `PackageFamilyName` 前缀
  （见 `DutyAgent.Client\Program.cs`；前缀必须拼在 `Local\`/`Global\` 根之后，
  `DutyAgent.Client.Tests\ScopeNameTests` 有回归）。
- 数据与日志都在 `%LOCALAPPDATA%\DutyAgent`，zip 版与商店版**共用同一份数据**；
  因此运行时另有一把**不带包前缀**的数据目录 owner 互斥（`Local\DutyAgent.DataDirectory.Owner`）：
  同机只允许一个客户端持有后端，后启动的一方通过 `Local\DutyAgent.DataDirectory.Wake`
  唤醒既有实例后退出，避免两个后端写坏共享的 `state/roster/meta`。
- 自启：包身份下若清单没有 `desktop6:startupTask` 扩展（当前如此，原因见清单注释），
  客户端回退写 `HKCU\...\Run`，值名为 `DutyAgent.<PFN>`（避免与 zip 版的裸值名
  `DutyAgent` 互相覆盖），命令为 `explorer.exe shell:AppsFolder\<PFN>!App`
  （实测：包身份写 Run 键不重定向、AUMID 命令可拉起客户端）；zip 版仍写 exe 路径。
  注意 **MSIX 自启无法传 `--silent`**：登录后显示主窗口而不是静默驻留托盘
  （AUMID/StartupTask 均不支持传参），已记录为已知差异。
- 握手 meta 三写双读，桥接（非包进程）从 Win32 `%LOCALAPPDATA%\ClassIsland\Config\DutyAgentBridge`
  读到的就是客户端写入的真实文件（实测 md5 一致，无重定向）。

## 能力声明说明

只声明了 `runFullTrust`。刻意**不**声明 `broadFileSystemAccess`：客户端的数据全部落在
`%LOCALAPPDATA%\DutyAgent`，与 ClassIsland 的握手 meta 文件采用多路径冗余写入
（宿主布局 / Win32 LocalAppData / 包 LocalState），无需访问包外任意路径。
少一个受限能力，审核摩擦小得多。

## 已知约束

- 仅 x64。arm64 需要另一份 `python-embed`，暂不支持。
- 未做 Store 商业化（内购/许可），也未接 Store 增量更新通道。
- 清单不声明任何 `<Extensions>`：本机工具链（NuGet BuildTools 的 makeappx schema 集）
  缺 Desktop schema，无法校验 `desktop:`/`desktop6:` 扩展元素。改用完整 Windows SDK
  构建时可以恢复 `startupTask` 扩展（清单注释里有现成 XML），Run 键回退逻辑继续兜底。
- WebView2 依赖 Evergreen Runtime。Win11 预装；Win10 绝大多数设备已随 Edge 更新获得，
  但仍有少量缺失（微软官方建议运行期检测）。客户端启动时会用
  `CoreWebView2Environment.GetAvailableBrowserVersionString()` 探测，缺失时给出官方
  下载指引，而不是抛一个底层 COM 异常。
