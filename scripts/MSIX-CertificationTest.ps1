#Requires -Version 5.1
<#
    .SYNOPSIS
        MSIX 包的静态认证检查（可在 CI 里跑，不需要管理员）。
    .DESCRIPTION
        补齐"MSIX 认证测试"空白：WACK 本机没有，商店认证又太晚，
        所以这里把能自动化的清单/资源/能力规范固化为一组断言。

        检查项：
          * 清单是合法 XML，Identity / Properties / Dependencies / Resources /
            Applications / VisualElements / Capabilities 结构齐全；
          * Identity.Version 是四段式（商店硬性要求），Publisher 形如 CN=；
          * 只声明 runFullTrust，绝不出现 broadFileSystemAccess（受限能力会显著增加审核摩擦）；
          * 清单里引用的每个资源文件在布局目录里真实存在；
          * TargetDeviceFamily.MinVersion 不低于声明的下限；
          * 未出现 <Extensions>（本机工具链无法校验 desktop:/desktop6: 扩展，属已知现状）；
          * 版本面与 Assets_Duty\version.py 是否漂移（默认只告警）。

        与 Test-MsisxLifecycle.ps1 的分工：本脚本只读不写，可在任意机器/CI 运行；
        生命周期脚本会真的装包、启动、卸载，只能手动加管理员跑。
    .PARAMETER ManifestPath
        仓库里的 Package.appxmanifest。
    .PARAMETER LayoutPath
        包布局（或解包后的包目录）。给了就顺带校验清单引用的文件都在。
    .PARAMETER VersionSource
        版本单一事实源，Assets_Duty\version.py。
    .PARAMETER RequireWebView2Dependency
        要求清单声明 Microsoft.WinWebView2Runtime 依赖。补齐后再打开。
    .PARAMETER EnforceStorePublisher
        要求 Publisher 形如 CN=<32 位十六进制>（Partner Center 发布者 ID）。上架前打开。
    .EXAMPLE
        .\Test-MsixPackage.ps1 -ManifestPath DutyAgent.Client.Package\Package.appxmanifest
    .EXAMPLE
        .\Test-MsixPackage.ps1 -ManifestPath .msix-work\final-unpack\AppxManifest.xml -LayoutPath .msix-work\final-unpack
#>
[CmdletBinding()]
param(
    [string]$ManifestPath = "DutyAgent.Client.Package\Package.appxmanifest",
    [string]$LayoutPath = "",
    [string]$VersionSource = "Assets_Duty\version.py",
    [switch]$RequireWebView2Dependency,
    [switch]$EnforceStorePublisher
)

$ErrorActionPreference = "Stop"

$script:Passed = 0
$script:Warnings = 0
$script:Failed = @()

function Write-Step([string]$Message) {
    Write-Host ""
    Write-Host "== $Message ==" -ForegroundColor Cyan
}

function Test-Assert([bool]$Condition, [string]$Message, [string]$Detail = "") {
    if ($Condition) {
        $script:Passed++
        Write-Host "  [PASS] $Message" -ForegroundColor Green
    }
    else {
        $script:Failed += $Message
        Write-Host "  [FAIL] $Message" -ForegroundColor Red
        if ($Detail) { Write-Host "         $Detail" -ForegroundColor DarkGray }
    }
    return $Condition
}

function Test-Warn([string]$Message) {
    $script:Warnings++
    Write-Host "  [WARN] $Message" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "===== Duty-Agent MSIX 认证检查（静态） =====" -ForegroundColor Cyan
Write-Host "  清单: $ManifestPath"
if ($LayoutPath) { Write-Host "  布局: $LayoutPath" }
Write-Host ""

# ---------------------------------------------------------------- 清单可解析
if (-not (Test-Path -LiteralPath $ManifestPath)) { throw "清单不存在：$ManifestPath" }
$ManifestPath = (Resolve-Path -LiteralPath $ManifestPath).Path

Write-Step "清单可解析"
$doc = $null
try {
    $text = [System.IO.File]::ReadAllText($ManifestPath, [System.Text.Encoding]::UTF8)
    [xml]$doc = $text
    Test-Assert $true "清单是合法 XML"
}
catch {
    Test-Assert $false "清单是合法 XML" $_.Exception.Message
    Write-Host ""
    Write-Host "清单解析失败，后续检查无法继续。" -ForegroundColor Red
    exit 1
}

$nsm = New-Object System.Xml.XmlNamespaceManager($doc.NameTable)
$nsm.AddNamespace("x", "http://schemas.microsoft.com/appx/manifest/foundation/windows10")
$nsm.AddNamespace("uap", "http://schemas.microsoft.com/appx/manifest/uap/windows10")
$nsm.AddNamespace("rescap", "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities")
$nsm.AddNamespace("uap10", "http://schemas.microsoft.com/appx/manifest/uap/windows10/10")

$identity = $doc.SelectSingleNode("/x:Package/x:Identity", $nsm)
$properties = $doc.SelectSingleNode("/x:Package/x:Properties", $nsm)
$dependencies = $doc.SelectSingleNode("/x:Package/x:Dependencies", $nsm)
$applications = $doc.SelectSingleNode("/x:Package/x:Applications", $nsm)
$capabilities = $doc.SelectSingleNode("/x:Package/x:Capabilities", $nsm)

# ---------------------------------------------------------------- Identity
Write-Step "Identity"
if (-not (Test-Assert ($null -ne $identity) "存在 <Identity>")) { exit 1 }

$idName = $identity.GetAttribute("Name")
Test-Assert (-not [string]::IsNullOrWhiteSpace($idName)) "Identity.Name 非空" $idName
# MSIX Identity.Name 只允许 字母/数字/./-（Store 同样限制）；出现下划线或中划线以外的
# 符号会在提交时被拒。本仓库 2026-10-14 从 DutyAgent 改成 HelemaZuba.Duty-Agent。
Test-Assert ($idName -match '^[A-Za-z0-9][A-Za-z0-9.\-]*$') "Identity.Name 只含 字母/数字/./- " $idName

$publisher = $identity.GetAttribute("Publisher")
Test-Assert ($publisher -like "CN=*") "Publisher 形如 CN=..." $publisher
# 两种形式都接受：Partner Center 有的账户显示 32 位十六进制，有的显示带连字符的 GUID。
$storePublisherPattern = '^CN=([0-9A-Fa-f]{32}|[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})$'
if ($EnforceStorePublisher) {
    Test-Assert ($publisher -match $storePublisherPattern) "Publisher 是 Partner Center 发布者 ID" $publisher
}
else {
    if ($publisher -match $storePublisherPattern) {
        Test-Warn "Publisher 是商店 ID。正式提交时请加 -EnforceStorePublisher 打开这条硬校验。"
    }
    else {
        Test-Warn "Publisher 是开发用 DN（侧载可，商店不可）。上架前换成 Partner Center 发布者 ID。"
    }
}

$version = $identity.GetAttribute("Version")
Test-Assert ($version -match "^\d+\.\d+\.\d+\.\d+$") "Version 是四段式（商店硬性要求）" $version
$v = $version.Split(".")
$numericParts = @($v | Where-Object { $_ -match "^\d+$" })
Test-Assert ($numericParts.Count -eq 4) "Version 四段全是数字" $version

$arch = $identity.GetAttribute("ProcessorArchitecture")
Test-Assert (-not [string]::IsNullOrWhiteSpace($arch) -and $arch -ne "neutral") "ProcessorArchitecture 已声明且非 neutral" $arch

# ---------------------------------------------------------------- Properties
Write-Step "Properties"
Test-Assert ($null -ne $properties) "存在 <Properties>"
Test-Assert (-not [string]::IsNullOrWhiteSpace($properties.GetElementsByTagName("DisplayName")[0].InnerText)) "DisplayName 非空"
Test-Assert (-not [string]::IsNullOrWhiteSpace($properties.GetElementsByTagName("PublisherDisplayName")[0].InnerText)) "PublisherDisplayName 非空"
Test-Assert (-not [string]::IsNullOrWhiteSpace($properties.GetElementsByTagName("Description")[0].InnerText)) "Description 非空"
$logo = $properties.GetElementsByTagName("Logo")
Test-Assert ($logo.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace($logo[0].InnerText)) "Logo 已声明"

# ---------------------------------------------------------------- Dependencies
Write-Step "Dependencies"
$tdf = $doc.SelectSingleNode("/x:Package/x:Dependencies/x:TargetDeviceFamily", $nsm)
if (Test-Assert ($null -ne $tdf) "存在 <TargetDeviceFamily>") {
    $tdfName = $tdf.GetAttribute("Name")
    Test-Assert ($tdfName -eq "Windows.Desktop") "TargetDeviceFamily 是 Windows.Desktop" $tdfName
    $minVersion = $tdf.GetAttribute("MinVersion")
    Test-Assert ($minVersion -match "^\d+\.\d+\.\d+\.\d+$") "MinVersion 形如 x.y.z.w" $minVersion
    # 本项目的支持下限：Windows 10 1903 (18362)
    $minParts = $minVersion.Split(".") | ForEach-Object { [int]$_ }
    Test-Assert ($minParts[2] -ge 18362) "MinVersion 不低于 10.0.18362.0（项目支持下限）" $minVersion
    $maxVersion = $tdf.GetAttribute("MaxVersionTested")
    Test-Assert (-not [string]::IsNullOrWhiteSpace($maxVersion)) "MaxVersionTested 已声明" $maxVersion
}

# ---------------------------------------------------------------- Resources
Write-Step "Resources"
$resources = $doc.SelectNodes("/x:Package/x:Resources/x:Resource", $nsm)
Test-Assert ($resources.Count -ge 1) "至少声明一个语言资源" ("共 " + $resources.Count)
$zh = $resources | Where-Object { $_.GetAttribute("Language") -eq "zh-CN" }
Test-Assert ($null -ne $zh) "包含 zh-CN（面向中文用户）"

# ---------------------------------------------------------------- Applications
Write-Step "Applications"
$app = $doc.SelectSingleNode("/x:Package/x:Applications/x:Application", $nsm)
if (Test-Assert ($null -ne $app) "存在 <Application>") {
    $exe = $app.GetAttribute("Executable")
    Test-Assert (-not [string]::IsNullOrWhiteSpace($exe)) "Application.Executable 非空" $exe
    $entryPoint = $app.GetAttribute("EntryPoint")
    Test-Assert ($entryPoint -eq "Windows.FullTrustApplication") "EntryPoint 是 Windows.FullTrustApplication" $entryPoint
    $appId = $app.GetAttribute("Id")
    Test-Assert (-not [string]::IsNullOrWhiteSpace($appId)) "Application.Id 非空" $appId
}

$visual = $doc.SelectSingleNode("/x:Package/x:Applications/x:Application/uap:VisualElements", $nsm)
if (Test-Assert ($null -ne $visual) "存在 <uap:VisualElements>") {
    foreach ($attr in @("Square150x150Logo", "Square44x44Logo", "DisplayName", "Description", "BackgroundColor")) {
        Test-Assert (-not [string]::IsNullOrWhiteSpace($visual.GetAttribute($attr))) "VisualElements.$attr 已声明"
    }
    $wide = $doc.SelectSingleNode("/x:Package/x:Applications/x:Application/uap:VisualElements/uap:DefaultTile", $nsm)
    Test-Assert ($null -ne $wide -and -not [string]::IsNullOrWhiteSpace($wide.GetAttribute("Wide310x150Logo"))) "DefaultTile.Wide310x150Logo 已声明"
}

# ---------------------------------------------------------------- Capabilities
Write-Step "Capabilities"
if (Test-Assert ($null -ne $capabilities) "存在 <Capabilities>") {
    $caps = $doc.SelectNodes("/x:Package/x:Capabilities/*", $nsm)
    $resCaps = $caps | Where-Object { $_.LocalName -eq "Capability" -and $_.NamespaceURI -like "*restrictedcapabilities*" }
    $names = @($resCaps | ForEach-Object { $_.GetAttribute("Name") })
    Test-Assert ($names -contains "runFullTrust") "声明了 runFullTrust（全信任桌面应用必需）" ($names -join ", ")
    Test-Assert ($names -notcontains "broadFileSystemAccess") "未声明 broadFileSystemAccess（少一个受限能力，少一分审核摩擦）" ($names -join ", ")
    $uapCaps = @($caps | Where-Object { $_.NamespaceURI -like "*manifest/uap/windows10*" } | ForEach-Object { $_.GetAttribute("Name") })
    if ($uapCaps.Count -gt 0) {
        Test-Warn ("存在 uap 能力: " + ($uapCaps -join ", ") + " —— 请确认它们确有用途。")
    }
}

# ---------------------------------------------------------------- Extensions
Write-Step "Extensions（已知工具链限制）"
$extensions = $doc.SelectSingleNode("/x:Package/x:Extensions", $nsm)
if ($null -eq $extensions) {
    Test-Assert $true "未声明 <Extensions>（与本机工具链能力一致，见清单注释）"
}
else {
    Test-Assert $false "不应出现 <Extensions>" "desktop:/desktop6: 扩展无法用 NuGet BuildTools / winapp 校验；若确有需要，请改用完整 Windows SDK 构建并同步更新 Package.appxmanifest 里的注释。"
}

# ---------------------------------------------------------------- WebView2 依赖
Write-Step "WebView2 运行时依赖"
$wv2 = $doc.SelectNodes("/x:Package/x:Dependencies/x:PackageDependency", $nsm) |
    Where-Object { $_.GetAttribute("Name") -eq "Microsoft.WinWebView2Runtime" }
if ($RequireWebView2Dependency) {
    Test-Assert ($null -ne $wv2 -and @($wv2).Count -ge 1) "清单声明了 Microsoft.WinWebView2Runtime 依赖" "Win10 未预装 WebView2，缺依赖界面打不开。"
}
else {
    if ($null -eq $wv2) {
        Test-Warn "未声明 Microsoft.WinWebView2Runtime 依赖：Win10 用户界面可能打不开。补齐后加 -RequireWebView2Dependency。"
    }
    else {
        Test-Assert $true "清单声明了 Microsoft.WinWebView2Runtime 依赖"
    }
}

# ---------------------------------------------------------------- 布局文件
if ($LayoutPath) {
    Write-Step "清单引用的文件是否真的在布局里"
    if (-not (Test-Path -LiteralPath $LayoutPath)) { throw "布局目录不存在：$LayoutPath" }
    $LayoutPath = (Resolve-Path -LiteralPath $LayoutPath).Path

    $referenced = @()
    foreach ($node in $doc.SelectNodes("//@*", $nsm) | Where-Object { $_.Name -like "*Logo" -or $_.Name -eq "Executable" -or $_.Name -eq "Logo" }) {
        $val = $node.InnerText
        if ($val -and $val -notmatch "^[A-Za-z]+:") { $referenced += $val }
    }
    # Logo 元素是子元素文本，不是属性
    if ($logo.Count -gt 0) { $referenced += $logo[0].InnerText }
    $referenced = $referenced | Where-Object { $_ } | Sort-Object -Unique
    foreach ($rel in $referenced) {
        $abs = Join-Path $LayoutPath ($rel -replace "/", "\")
        Test-Assert (Test-Path -LiteralPath $abs) "布局中存在清单引用的文件：$rel"
    }

    $webApp = Join-Path $LayoutPath "Assets_Duty\web"
    Test-Assert (Test-Path -LiteralPath $webApp) "布局中包含前端 web 资源" $webApp

    $python = Join-Path $LayoutPath "Assets_Duty\python-embed\python.exe"
    Test-Assert (Test-Path -LiteralPath $python) "布局中包含内嵌 Python 解释器" $python
}

# ---------------------------------------------------------------- 版本面
Write-Step "版本单一事实源"
if ($VersionSource -and (Test-Path -LiteralPath $VersionSource)) {
    $VersionSource = (Resolve-Path -LiteralPath $VersionSource).Path
    $pyText = [System.IO.File]::ReadAllText($VersionSource, [System.Text.Encoding]::UTF8)
    $m = [regex]::Match($pyText, '(?m)^APP_VERSION[ \t]*=[ \t]*"([^"\r\n]*)"')
    if ($m.Success) {
        $src = $m.Groups[1].Value
        $srcFs = $src -replace '^(\d+\.\d+\.\d+)$', '$1.0'
        if ($srcFs -eq $version) {
            Test-Assert $true "清单 Identity.Version 与 $VersionSource 一致" ($version)
        }
        else {
            # 只告警不失败：这是"上架版本号统一"的待决策项，
            # 不阻塞本次交付。商店不接受版本回退，所以这个决策必须显式做。
            Test-Warn ("版本面漂移：version.py=$src -> 期望四段 $srcFs；清单=$version。" +
                       "需要定一个统一版本号并同步 Assets_Duty/version.py、清单、桥接 csproj/manifest.yml、README。")
        }
    }
    else {
        Test-Warn "没能从 $VersionSource 解析出 APP_VERSION。"
    }
}

# ---------------------------------------------------------------- 汇总
Write-Host ""
Write-Host "============================================================"
Write-Host (" 认证检查完成：通过 {0} 项，失败 {1} 项，警告 {2} 项" -f $script:Passed, $script:Failed.Count, $script:Warnings)
if ($script:Failed.Count -gt 0) {
    Write-Host " 失败项：" -ForegroundColor Red
    foreach ($f in $script:Failed) { Write-Host "   - $f" -ForegroundColor Red }
    Write-Host "============================================================"
    exit 1
}
Write-Host " 全部通过。"
Write-Host "============================================================"
exit 0
