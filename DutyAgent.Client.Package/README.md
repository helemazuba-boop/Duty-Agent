# DutyAgent.Client MSIX 打包工程

把独立客户端打成可提交微软商店（Partner Center）的 MSIX 包。

> ✅ **本机已实证**（2026-10-13）：用 `@microsoft/winappcli`（winapp 0.7.1）在本机产出并
> 解包验证了 `.msix`。**不需要 Visual Studio、不需要 Windows Kits**，构建机只要有
> .NET SDK + Node/npm 即可。实测数据见 `docs/msix-store-adaptation-plan.md` 修正 B。

## 构建

```powershell
# 一条命令：发布客户端 → 组装 winapp 布局 → winapp package → 开发证书签名
powershell -ExecutionPolicy Bypass -File scripts\Build-MsixPackage.ps1

# 指定版本：只改 MSIX 的 Identity.Version（补 .0），别处一律不动。
# 版本面已分家，别再混（2026-10-14 修）：
#   包版本  = 这里的 -ReleaseVersion，或清单里的默认值；
#   引擎版本 = Assets_Duty\version.py 的 APP_VERSION，后端自己的版本，与此无关。
powershell -ExecutionPolicy Bypass -File scripts\Build-MsixPackage.ps1 -ReleaseVersion 1.2.3

# 商店提交模式：不本地签名（交给 Partner Center）
powershell -ExecutionPolicy Bypass -File scripts\Build-MsixPackage.ps1 -ForStore
```

产物在 `.msix-work\`：`DutyAgent_<版本>_x64.msix`（开发证书签名）或同名未签名包（`-ForStore`）。
布局暂存目录为 `.msix-work\pkg`，证书在 `.msix-work\cert\devcert.pfx`
（口令是 winapp 默认的 `password`；公钥另导出 `devcert.cer`，旁加载和 CI 只需要它）。

> 性能提示：`winapp package` 会先把约 260MB 的布局复制到 `%TEMP%` 再压缩。
> C: 剩余空间不足时该阶段会慢到看似假死（实测 19 分钟未完成）；脚本已把
> `TMP`/`TEMP` 指到 `.msix-work\temp`，同体积实测约 20 秒。

## 提交商店前必须做的事

> ✅ **包身份已换成正式上架配置**（2026-10-14）：
> `Identity Name="HelemaZuba.Duty-Agent"` / `Publisher="CN=B4407923-BF46-468D-B8F8-549EDD96BDC3"`。
> **PFN 随之改变**（不再是 `DutyAgent_3ecm5aq137n80`），AUMID = 新 `<PFN>!App`。
> 本地签名用的开发证书会自动按新 Publisher 重新生成（`Build-MsixPackage.ps1` 会核对）。
> 提交前请确认这个 Publisher 与 Partner Center → 账户设置里给出的发布者 ID **逐字符一致**
> （含连字符），否则商店会拒。

1. **核对发布者 ID 与应用名**：确认上面的 `Name` / `Publisher` 与 Partner Center 里预留的
   完全一致，然后用 `-ForStore` 交付（打包时不本地签名，由商店签名）。
2. **检查商店图标资产**。`Assets\*.png` 已由 `winapp manifest update-assets <仓库根>\icon.png`
   生成 49 张（MedTile / AppList / WideTile / StoreLogo 全尺寸），已入库；
   换图标后重跑该命令即可，无需手工缩放。
3. **年龄分级与隐私政策 URL**。应用会把名单姓名发往用户配置的 LLM API，隐私条款必须覆盖。
4. **补 WebView2 运行时依赖**。清单当前**没有**声明 `Microsoft.WinWebView2Runtime`，
   Windows 10 默认不带这个运行时，缺了界面打不开（客户端会弹"启动失败"对话框，
   托盘和后端仍正常）。补齐依赖后把 `MSIX-CertificationTest.ps1 -RequireWebView2Dependency`
   打开。旁加载场景声明该依赖会导致装不上，所以验证包里做的是"检测 + 提示"而非硬依赖。
5. 商店侧提交 `.msix`（或打成 `.msixupload`）；认证通常 1–3 天。

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

## 认证检查与生命周期验收

| 脚本 | 作用 | 环境要求 |
|---|---|---|
| `scripts\MSIX-CertificationTest.ps1` | 静态认证：清单结构、能力、资源文件、版本面。只读不写。 | 无（CI 里跑） |
| `scripts\MSIX-LifecycleTest.ps1` | 正式生命周期：安装 → 首跑（AUMID 激活、包身份日志、Run 键自启、数据落点）→ 升级（版本递增、数据保留）→ 卸载（残留、数据不删）。 | **管理员 + 真机** |

```powershell
# 静态认证（任何 PR / CI 都能跑）
.\scripts\MSIX-CertificationTest.ps1 -ManifestPath DutyAgent.Client.Package\Package.appxmanifest

# 对已解包的包体再跑一遍（包内清单才是真正被安装/认证读到的那一份，
# winapp 会重排属性并注入 build:Metadata，仓库里那份通过不代表包体没问题）
.\scripts\MSIX-CertificationTest.ps1 -ManifestPath .msix-work\final-unpack\AppxManifest.xml -LayoutPath .msix-work\final-unpack

# 生命周期验收（会装包、写注册表、卸载，默认要交互确认；-Force 跳过）
.\scripts\MSIX-LifecycleTest.ps1 -MsixPath .msix-work\DutyAgent_1.0.0.0_x64.msix -CertPath .msix-work\cert\devcert.cer
```

这些检查由 `.github\workflows\msix.yml` 在每次推送/PR 到 MSIX 相关路径时自动执行：
静态认证 → 出包 → 按包内清单再认证 → 装/卸往返。GUI 启动部分刻意不在 CI 里做
（服务器版没有交互会话，WebView2 也未必在），由 `MSIX-LifecycleTest.ps1` 手动覆盖。

> 升级验收要第二个包。不给 `-UpgradeMsixPath` 时该阶段只告警不失败。
> 生成方法：先出 1.0.0.1 的包，再把两个包一起传给脚本。

## 与 zip 版的关系

- **zip 版与商店版不能同时跑。** 单实例互斥体名与唤醒事件名在包身份下会加
  `PackageFamilyName` 前缀（见 `DutyAgent.Client\Program.cs`；前缀必须拼在
  `Local\`/`Global\` 根之后，`DutyAgent.Client.Tests\ScopeNameTests` 有回归），
  所以两者**互相挡不住**：会同时拉起后端、抢同一个端口。
  数据与日志都在 `%LOCALAPPDATA%\DutyAgent`，两边共用同一份数据——
  同一时刻只应有一个在跑，否则端口和握手 meta 都会互相污染。
- 自启：包身份下若清单没有 `desktop6:startupTask` 扩展（当前如此，原因见清单注释），
  客户端回退写 `HKCU\...\Run`，命令为 `explorer.exe shell:AppsFolder\<PFN>!App`
  （实测：包身份写 Run 键不重定向、AUMID 命令可拉起客户端）；zip 版仍写 exe 路径。
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
- WebView2 依赖 Evergreen Runtime：**Windows 11 预装，Windows 10 通常没有**
  （可能因装了别的软件而顺带存在）。缺了不会崩，客户端弹"启动失败"对话框，
  托盘与后端照常运行。运行时未打进包，也没在清单里声明依赖（见上面第 4 条）。
