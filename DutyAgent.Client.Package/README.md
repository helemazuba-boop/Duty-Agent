# DutyAgent.Client MSIX 打包工程

把独立客户端打成可提交微软商店（Partner Center）的 MSIX 包。

> ⚠️ **本机当前无法产出 .msix**：本机没有安装 Windows Kits（`C:\Program Files (x86)\Windows Kits` 不存在）、
> 没有 MakeAppx/SignTool，`.NET` SDK 中的 MSBuild 也不含 DesktopBridge 目标。
> 需要先安装 VS 2022 的 "Windows application development" 工作负载（含 Desktop Bridge）
> 或独立 Windows SDK，再执行下面的构建命令。首包产出前，本目录下的清单与工程文件
> 属于"可审阅但未运行验证"状态。

## 构建

```powershell
# 一条命令：发布客户端 → 组装 MSIX → 本地签名（临时证书）
powershell -ExecutionPolicy Bypass -File scripts\Build-MsixPackage.ps1

# 指定版本（同步盖 version.py / duty-agent.exe / 桥接 manifest，与发布脚本一致）
powershell -ExecutionPolicy Bypass -File scripts\Build-MsixPackage.ps1 -ReleaseVersion 1.2.3
```

产物在 `DutyAgent.Client.Package\AppPackages\`：`.msix`（本地侧载/上传用）与 `.msixupload`（提交商店）。

## 提交商店前必须做的事

1. **替换发布者标识**。打开 `Package.appxmanifest` 与 `DutyAgent.Client.Package.wapproj`，把
   `CN=REPLACE_WITH_PARTNER_CENTER_PUBLISHER_ID` 换成 Partner Center → 账户设置里给出的发布者 ID
   （形如 `CN=` + 32 位十六进制）。两处必须一致，否则签名校验失败。
2. **准备商店图标资产**。`scripts\Build-MsixPackage.ps1` 在没有检测到 `Assets\*.png` 时会用
   仓库根 `icon.png` 缩放出占位图（仅保证能构建）。正式提交请按商店要求替换：
   `Square44x44Logo.png` / `Square150x150Logo.png` / `StoreLogo.png`，
   需要透明背景、多尺寸、正确 DPI。
3. **年龄分级与隐私政策 URL**。应用会把名单姓名发往用户配置的 LLM API，隐私条款必须覆盖。
4. 商店侧提交 `.msixupload` 或上传 .msix；认证通常 1–3 天。

## 本地侧载测试

```powershell
Add-AppxPackage -Path .\DutyAgent.Client.Package\AppPackages\DutyAgent_1.0.0.0_x64.msix
# 卸载
Get-AppxPackage DutyAgent | Remove-AppxPackage
```

## 与 zip 版的关系

- 两种形态可以同机安装运行：单实例互斥体名与唤醒事件名在包身份下会加 `PackageFamilyName` 前缀，
  互不干扰（见 `DutyAgent.Client\Program.cs`）。
- 数据与日志都在 `%LOCALAPPDATA%\DutyAgent`，zip 版与商店版**共用同一份数据**；
  同一时刻只应有一个在跑，否则会抢后端端口。
- 自启在包身份下由系统 `StartupTask` 管理（设置 → 应用 → 启动），zip 版仍写 `HKCU\...\Run`。

## 能力声明说明

只声明了 `runFullTrust`。刻意**不**声明 `broadFileSystemAccess`：客户端的数据全部落在
`%LOCALAPPDATA%\DutyAgent`，与 ClassIsland 的握手 meta 文件采用双写（含 LocalAppData 备路径），
无需访问包外任意路径。少一个受限能力，审核摩擦小得多。

## 已知约束

- 仅 x64。arm64 需要另一份 `python-embed`，暂不支持。
- 未做 Store 商业化（内购/许可），也未接 Store 增量更新通道。
- `.wapproj` 只能用 VS 的 MSBuild 构建；仓库其余 C# 项目仍用 `dotnet build`。
