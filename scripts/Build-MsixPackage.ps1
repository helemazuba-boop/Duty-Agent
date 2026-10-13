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
# 两个版本面分开，别再混：
#   * MSIX Identity.Version = 产品/包版本，由 ReleaseVersion 或清单里的默认值决定；
#   * Assets_Duty\version.py 的 APP_VERSION = 后端引擎版本，两回事。
# 历史上这里强行用 version.py 去改包版本，出过两个坑（2026-10-14 修）：
#   a) 那条 Regex.Replace(text, 'Version="\d+\.\d+\.\d+\.\d+"', ..., 1) 是"跑了但没生效"——
#      多行属性排版下第一个匹配根本不是 Identity.Version；
#   b) PS 5.1 下 int 参数找不到 count 重载，会被解释成 RegexOptions.IgnoreCase，
#      于是改成"全部替换"，顺手把 TargetDeviceFamily 的 MinVersion 也一起换掉 ——
#      那种包装得上但行为完全不对，比报错更难查。
# 现在只有显式给了 -ReleaseVersion（CI / release.yml 的 tag 流程会传）才盖章，
# 否则沿用清单里的版本，绝不动。
$stagedManifest = Join-Path $pkgDir "Package.appxmanifest"

# 盖章前先记下原值，改完要复读比对
$preXml = New-Object System.Xml.XmlDocument
$preXml.PreserveWhitespace = $true
$preXml.Load($stagedManifest)
$preNsm = New-Object System.Xml.XmlNamespaceManager($preXml.NameTable)
$preNsm.AddNamespace("x", "http://schemas.microsoft.com/appx/manifest/foundation/windows10")
$preIdentity = $preXml.SelectSingleNode("/x:Package/x:Identity", $preNsm)
if ($null -eq $preIdentity) { throw "清单里找不到 <Identity>，无法处理版本。" }
$manifestVersion = $preIdentity.GetAttribute("Version")
# Publisher 也要读出来：下面签证书要以它为准。Publisher 一变，旧开发证书立刻作废。
$publisher = $preIdentity.GetAttribute("Publisher")
$identityName = $preIdentity.GetAttribute("Name")
$preMinVersion = $preXml.SelectSingleNode("/x:Package/x:Dependencies/x:TargetDeviceFamily", $preNsm).GetAttribute("MinVersion")

if ([string]::IsNullOrWhiteSpace($ReleaseVersion)) {
    Write-Host "[msix] 未指定 -ReleaseVersion，沿用清单版本 $manifestVersion（不触碰 Assets_Duty\version.py 的引擎版本面）"
    $msixVersion = $manifestVersion
}
else {
    if ($ReleaseVersion -notmatch '^\d+\.\d+\.\d+$') {
        throw "ReleaseVersion '$ReleaseVersion' 必须形如 x.y.z（四段写法请自行补 .0 到版本面里）。"
    }
    $msixVersion = "$ReleaseVersion.0"
    $preIdentity.SetAttribute("Version", $msixVersion)
    $preXml.Save($stagedManifest)
    Write-Host "[msix] 版本: $msixVersion（来自 -ReleaseVersion $ReleaseVersion）"

    # 复读校验：上次的教训就是"代码跑了但文件没变"
    $postXml = New-Object System.Xml.XmlDocument
    $postXml.Load($stagedManifest)
    $postNsm = New-Object System.Xml.XmlNamespaceManager($postXml.NameTable)
    $postNsm.AddNamespace("x", "http://schemas.microsoft.com/appx/manifest/foundation/windows10")
    $postIdentity = $postXml.SelectSingleNode("/x:Package/x:Identity", $postNsm)
    $gotVersion = $postIdentity.GetAttribute("Version")
    if ($gotVersion -ne $msixVersion) { throw "版本盖章失败：期望 $msixVersion，实际 $gotVersion" }
    $gotMinVersion = $postXml.SelectSingleNode("/x:Package/x:Dependencies/x:TargetDeviceFamily", $postNsm).GetAttribute("MinVersion")
    if ($gotMinVersion -ne $preMinVersion) { throw "版本盖章误伤了 MinVersion：$preMinVersion -> $gotMinVersion" }
    Write-Host "[msix] 版本已盖章并复读校验（MinVersion 未受影响：$gotMinVersion）"
}

# ---------- 5. 打包 ----------
$packageArgs = @("package", $pkgDir, "--manifest", $stagedManifest, "--name", $identityName)
if ($ForStore) {
    $packageArgs += "--no-sign"
    Write-Host "[msix] ForStore 模式：包不本地签名，交给 Partner Center 签名。"
    Write-Host "[msix] 当前 Publisher: $publisher"
    Write-Warning "提交前请确认这个 Publisher 与 Partner Center → 账户设置里给出的发布者 ID 完全一致（含连字符），否则商店会拒绝。"
} else {
    # 开发证书：Publisher 必须与清单 Identity.Publisher 完全一致，否则 winapp
    # 打包传 --cert 时会因为"签名者与包身份不匹配"而失败（报错还不明显）。
    # 换 Name/Publisher（2026-10-14 从开发 DN 换成正式发布者 ID）后旧证书即作废，
    # 所以这里按 publisher.txt 里的记录核对，不一致就重新生成。
    $certDir = Join-Path $workRoot "cert"
    $devCert = Join-Path $certDir "devcert.pfx"
    $devCer = Join-Path $certDir "devcert.cer"
    $certPublisherFile = Join-Path $certDir "publisher.txt"

    $certPublisher = $null
    if (Test-Path -LiteralPath $certPublisherFile) {
        $certPublisher = [System.IO.File]::ReadAllText($certPublisherFile, [System.Text.Encoding]::UTF8).Trim()
    }

    $needCert = (-not (Test-Path -LiteralPath $devCert)) -or ($certPublisher -ne $publisher)
    if ($needCert) {
        New-Item -ItemType Directory -Force -Path $certDir | Out-Null
        if ($null -ne $certPublisher) {
            Write-Host "[msix] 开发证书 Publisher 已变（$certPublisher -> $publisher），重新生成。"
        } else {
            Write-Host "[msix] 生成开发证书（Publisher 从清单推断）..."
        }
        Push-Location $certDir
        try {
            # --export-cer：同时导出只含公钥的 .cer。侧载/CI 装包只需要 .cer，
            # 不必把带私钥的 .pfx（口令是 winapp 默认的 "password"）拷来拷去。
            # --if-exists overwrite：覆盖而不是先删（本机对仓库内文件删除会挂起）。
            & $winappCmd cert generate --manifest $stagedManifest --export-cer --output $devCert --if-exists overwrite -v
            if ($LASTEXITCODE -ne 0) { throw "生成开发证书失败" }
            & $winappCmd cert install $devCert -v
            if ($LASTEXITCODE -ne 0) { Write-Warning "证书安装失败，侧载时可能需要手动信任。" }
        } finally {
            Pop-Location
        }
        [System.IO.File]::WriteAllText($certPublisherFile, $publisher, (New-Object System.Text.UTF8Encoding($false)))
        if (Test-Path -LiteralPath $devCer) {
            Write-Host "[msix] 开发证书公钥: $devCer"
        }
    } else {
        Write-Host "[msix] 复用已有开发证书（Publisher 与清单一致）。"
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
