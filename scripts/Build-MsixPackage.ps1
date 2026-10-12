[CmdletBinding()]
param(
    # x.y.z[.w]，与 New-DutyAgentClientRelease.ps1 同一契约：四处版本面必须一致。
    [string]$ReleaseVersion = "",
    [string]$Configuration = "Release",
    [switch]$SkipWebBuild,
    # 没有 Partner Center 发布者 ID 时，生成本地侧载用的临时签名调试包。
    [switch]$LocalDebugSign
)

$ErrorActionPreference = "Stop"

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$clientProject = Join-Path $root "DutyAgent.Client\DutyAgent.Client.csproj"
$packageProject = Join-Path $root "DutyAgent.Client.Package\DutyAgent.Client.Package.wapproj"
$packageDir = Join-Path $root "DutyAgent.Client.Package"
$stagingRoot = Join-Path $packageDir "staging"

if ($ReleaseVersion -and $ReleaseVersion -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
    throw "ReleaseVersion '$ReleaseVersion' 不是合法的 x.y.z[.w]。"
}

# ---------- 1. 发布客户端 ----------
# 复用既有发布脚本：它负责构建前端、发布客户端、组装 Assets_Duty（含 85M 内嵌 Python）
# 与随包桥接插件，并统一四处版本面。MSIX 打包只需要其中的 client 目录。
Write-Host "[msix] 发布客户端（复用 New-DutyAgentClientRelease.ps1）..."
if (-not (Test-Path -LiteralPath $clientProject)) { throw "Missing client project: $clientProject" }
New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null

$releaseArgs = @(
    "-OutputRoot", $stagingRoot,
    "-Configuration", $Configuration,
    "-NoZip"
)
if ($ReleaseVersion) { $releaseArgs += @("-ReleaseVersion", $ReleaseVersion) }
if ($SkipWebBuild) { $releaseArgs += "-SkipWebBuild" }

& (Join-Path $PSScriptRoot "New-DutyAgentClientRelease.ps1") @releaseArgs
if ($LASTEXITCODE -ne 0) { throw "发布脚本失败，exit=$LASTEXITCODE" }

# 发布脚本每次生成唯一的时间戳目录，取最新的那个 client。
$publishDir = Get-ChildItem -LiteralPath $stagingRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "client") } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1 |
    ForEach-Object { Join-Path $_.FullName "client" }

if (-not $publishDir -or -not (Test-Path -LiteralPath (Join-Path $publishDir "duty-agent.exe"))) {
    throw "未找到发布产物：$stagingRoot 下没有包含 duty-agent.exe 的 client 目录。"
}
Write-Host "[msix] 客户端产物: $publishDir"

# ---------- 2. 版本 ----------
$version = $ReleaseVersion
if ([string]::IsNullOrWhiteSpace($version)) {
    $versionPy = Join-Path $root "Assets_Duty\version.py"
    if (Test-Path -LiteralPath $versionPy) {
        $text = [System.IO.File]::ReadAllText($versionPy, [System.Text.Encoding]::UTF8)
        $m = [System.Text.RegularExpressions.Regex]::Match($text, '(?m)^APP_VERSION[ \t]*=[ \t]*"([^"\r\n]*)"')
        if ($m.Success) { $version = $m.Groups[1].Value }
    }
}
if ([string]::IsNullOrWhiteSpace($version)) { $version = "1.0.0" }
# MSIX Identity Version 必须是四段式。
$msixVersion = $version -replace '^(\d+\.\d+\.\d+)$', '$1.0'
Write-Host "[msix] 版本: $version (包版本 $msixVersion)"

# ---------- 3. 商店图标资产：缺失时用仓库 icon.png 缩放占位 ----------
$assetsDir = Join-Path $packageDir "Assets"
New-Item -ItemType Directory -Force -Path $assetsDir | Out-Null
$requiredAssets = @("Square44x44Logo.png", "Square150x150Logo.png", "StoreLogo.png")
$iconSource = Join-Path $root "icon.png"
$warned = $false

Add-Type -AssemblyName System.Drawing
foreach ($asset in $requiredAssets) {
    $target = Join-Path $assetsDir $asset
    if (Test-Path -LiteralPath $target) { continue }
    if (-not (Test-Path -LiteralPath $iconSource)) {
        throw "缺少商店资产 $asset，且仓库根没有 icon.png 可缩放。请手工放置商店资产。"
    }
    if (-not $warned) {
        Write-Warning "未找到商店资产，已用仓库根 icon.png 缩放生成占位图；正式提交商店前必须替换为合规的多尺寸透明图标。"
        $warned = $true
    }

    $size = switch -Regex ($asset) {
        '44' { 44 }
        '150' { 150 }
        default { 50 }
    }
    $src = [System.Drawing.Image]::FromFile($iconSource)
    try {
        $bmp = New-Object System.Drawing.Bitmap($size, $size)
        try {
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            try {
                $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $g.DrawImage($src, 0, 0, $size, $size)
            }
            finally { $g.Dispose() }
            $bmp.Save($target, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally { $bmp.Dispose() }
    }
    finally { $src.Dispose() }
}

# ---------- 4. 定位带 Desktop Bridge 的 MSBuild ----------
# .NET SDK 的 MSBuild 没有 Microsoft.DesktopBridge.targets，必须用 VS 自带的那个。
$candidates = @()
foreach ($base in @("${env:ProgramFiles(x86)}\Microsoft Visual Studio", "${env:ProgramFiles}\Microsoft Visual Studio")) {
    if (Test-Path -LiteralPath $base) {
        $candidates += Get-ChildItem -LiteralPath $base -Recurse -Depth 3 -Filter "MSBuild.exe" -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -like "*\Bin\MSBuild.exe" } |
            Sort-Object FullName -Descending
    }
}
$vsMsBuild = $candidates | Select-Object -First 1

if ($null -eq $vsMsBuild) {
    throw @"
未找到 Visual Studio 的 MSBuild。构建 MSIX 需要 Desktop Bridge 工作负载（Windows SDK + MakeAppx/SignTool）。

请二选一：
  1) Visual Studio Installer → 修改 → 工作负载，勾选
     「使用 C++ 的桌面开发」与「通用 Windows 平台开发」（含 MSIX 打包工具）
  2) 安装独立 Windows SDK：https://developer.microsoft.com/windows/downloads/windows-sdk/

安装后重跑本脚本。
"@
}
Write-Host "[msix] 使用 MSBuild: $($vsMsBuild.FullName)"

# ---------- 5. 构建 MSIX ----------
$buildArgs = @(
    $packageProject,
    "/t:Restore,Build",
    "/p:Configuration=$Configuration",
    "/p:Platform=x64",
    "/p:Version=$msixVersion",
    "/p:PublishDir=$publishDir"
)
if ($LocalDebugSign) {
    $buildArgs += "/p:AppxPackageSigningEnabled=true"
    $buildArgs += "/p:GenerateTemporaryStoreCertificate=true"
}

Push-Location $packageDir
try {
    & $vsMsBuild.FullName @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "MSBuild 失败，exit=$LASTEXITCODE" }
}
finally {
    Pop-Location
}

$appPackages = Join-Path $packageDir "AppPackages"
$msixFiles = @(Get-ChildItem -LiteralPath $appPackages -Recurse -Filter "*.msix" -ErrorAction SilentlyContinue)
if ($msixFiles.Count -eq 0) {
    throw "构建报告成功但未找到 .msix，请检查 $appPackages 下的 MSBuild 日志。"
}

Write-Host ""
Write-Host "============================================================"
Write-Host " MSIX build done"
foreach ($f in $msixFiles) { Write-Host " $($f.FullName)" }
Write-Host "============================================================"
