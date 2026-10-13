[CmdletBinding()]
param(
    # x.y.z[.w]，与 New-DutyAgentClientRelease.ps1 同一契约：多处版本面必须一致。
    [string]$ReleaseVersion = "",
    [string]$Configuration = "Release",
    [switch]$SkipWebBuild,
    # 商店提交模式：不本地签名（交给 Partner Center），并提醒替换 Publisher。
    [switch]$ForStore
)

$ErrorActionPreference = "Stop"

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$clientProject = Join-Path $root "DutyAgent.Client\DutyAgent.Client.csproj"
$packageDir = Join-Path $root "DutyAgent.Client.Package"
$manifestSource = Join-Path $packageDir "Package.appxmanifest"
$assetsSource = Join-Path $packageDir "Assets"
$workRoot = Join-Path $root ".msix-work"
$stagingRoot = Join-Path $workRoot "release-staging"
$pkgDir = Join-Path $workRoot "pkg"
$tempDir = Join-Path $workRoot "temp"

if ($ReleaseVersion -and $ReleaseVersion -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
    throw "ReleaseVersion '$ReleaseVersion' 不是合法的 x.y.z[.w]。"
}
if (-not (Test-Path -LiteralPath $manifestSource)) { throw "Missing manifest: $manifestSource" }
if (-not (Test-Path -LiteralPath $assetsSource)) { throw "Missing assets dir: $assetsSource" }

# ---------- 0. 关键环境：把 %TEMP% 挪到仓库所在盘 ----------
# winapp package 会先把整个包布局（约 260MB）复制到 %TEMP% 再调用 makeappx 压缩。
# C: 剩余空间不足时这一阶段会慢到看似假死（实测 19 分钟未完成）；
# 指到空闲盘后同样体积只需约 20 秒。
New-Item -ItemType Directory -Force -Path $tempDir | Out-Null
$env:TMP = $tempDir
$env:TEMP = $tempDir

# ---------- 1. 定位 winapp CLI ----------
$winapp = Get-Command winapp -ErrorAction SilentlyContinue
if ($null -eq $winapp) {
    $npmGlobal = Join-Path $env:APPDATA "npm\node_modules\@microsoft\winappcli\bin\win-x64\winapp.exe"
    if (Test-Path -LiteralPath $npmGlobal) {
        $winappCmd = $npmGlobal
    } else {
        throw "找不到 winapp CLI。请先安装：npm install -g @microsoft/winappcli"
    }
} else {
    $winappCmd = $winapp.Source
}
Write-Host "[msix] winapp: $winappCmd"

# ---------- 2. 发布客户端 ----------
# 复用既有发布脚本：构建前端、发布客户端、组装 Assets_Duty（含内嵌 Python）
# 与随包桥接插件。MSIX 只需要其中的 client 目录 + bridge 目录。
Write-Host "[msix] 发布客户端（复用 New-DutyAgentClientRelease.ps1）..."
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

$release = Get-ChildItem -LiteralPath $stagingRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "client\duty-agent.exe") } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if ($null -eq $release) {
    throw "未找到发布产物：$stagingRoot 下没有包含 duty-agent.exe 的 client 目录。"
}
$clientReleaseDir = Join-Path $release.FullName "client"
$bridgeReleaseDir = Join-Path $release.FullName "bridge"
Write-Host "[msix] 客户端产物: $clientReleaseDir"

# ---------- 3. 组装 winapp 包布局 ----------
# winapp 接受"发布目录 + 同级的 Package.appxmanifest 与 Assets"布局。
# 直接用发布产物本身，不复制（winapp 会自己复制到 %TEMP%）。
if (Test-Path -LiteralPath $pkgDir) { Remove-Item -LiteralPath $pkgDir -Recurse -Force }
Move-Item -LiteralPath $clientReleaseDir -Destination $pkgDir

Copy-Item -LiteralPath $manifestSource -Destination (Join-Path $pkgDir "Package.appxmanifest") -Force
Copy-Item -LiteralPath $assetsSource -Destination (Join-Path $pkgDir "Assets") -Recurse -Force

# 随包桥接插件：客户端UI"一键安装"时从包内读取它。
if (Test-Path -LiteralPath $bridgeReleaseDir) {
    Copy-Item -LiteralPath $bridgeReleaseDir -Destination (Join-Path $pkgDir "bridge") -Recurse -Force
} else {
    Write-Warning "发布产物里没有 bridge 目录，包内将不含随包桥接插件。"
}

# ---------- 4. 版本盖章 ----------
# MSIX Identity Version 必须是四段式；发布脚本已统一其余版本面。
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
$msixVersion = $version -replace '^(\d+\.\d+\.\d+)$', '$1.0'
$stagedManifest = Join-Path $pkgDir "Package.appxmanifest"
$manifestText = [System.IO.File]::ReadAllText($stagedManifest, [System.Text.Encoding]::UTF8)
$manifestText = [System.Text.RegularExpressions.Regex]::Replace(
    $manifestText, 'Version="\d+\.\d+\.\d+\.\d+"', "Version=`"$msixVersion`"", 1)
[System.IO.File]::WriteAllText($stagedManifest, $manifestText, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "[msix] 版本: $version (包版本 $msixVersion)"

# ---------- 5. 打包 ----------
$packageArgs = @("package", $pkgDir, "--manifest", $stagedManifest, "--name", "DutyAgent")
if ($ForStore) {
    $packageArgs += "--no-sign"
    Write-Warning "ForStore 模式：包未本地签名，交给 Partner Center 签名。提交前必须把 Package.appxmanifest 的 Publisher 换成账户里的 32 位十六进制发布者 ID。"
} else {
    $certDir = Join-Path $workRoot "cert"
    $devCert = Join-Path $certDir "devcert.pfx"
    if (-not (Test-Path -LiteralPath $devCert)) {
        New-Item -ItemType Directory -Force -Path $certDir | Out-Null
        Write-Host "[msix] 生成开发证书（Publisher 从清单推断）..."
        Push-Location $certDir
        try {
            & $winappCmd cert generate --manifest $stagedManifest -v
            if ($LASTEXITCODE -ne 0) { throw "生成开发证书失败" }
            & $winappCmd cert install (Join-Path $certDir "devcert.pfx") -v
            if ($LASTEXITCODE -ne 0) { Write-Warning "证书安装失败，侧载时可能需要手动信任。" }
        } finally {
            Pop-Location
        }
    }
    $packageArgs += @("--cert", $devCert)
}

Write-Host "[msix] 打包（布局目录 $pkgDir）..."
& $winappCmd @packageArgs -v
if ($LASTEXITCODE -ne 0) { throw "winapp package 失败，exit=$LASTEXITCODE" }

$msixFiles = @(Get-ChildItem -LiteralPath $workRoot -Filter "*.msix" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending)
if ($msixFiles.Count -eq 0) { throw "打包报告成功但未找到 .msix，请检查 $workRoot。" }

Write-Host ""
Write-Host "============================================================"
Write-Host " MSIX build done"
foreach ($f in $msixFiles) { Write-Host " $($f.FullName)" }
if (-not $ForStore) {
    Write-Host " 本地验证：winapp create-debug-identity $pkgDir\duty-agent.exe --manifest $stagedManifest"
    Write-Host " 然后 shell:AppsFolder\<PackageFamilyName>!App 启动。"
}
Write-Host "============================================================"
