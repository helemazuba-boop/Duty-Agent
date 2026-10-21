[CmdletBinding()]
param(
    # x.y.z[.w]，与 New-DutyAgentClientRelease.ps1 同一契约：多处版本面必须一致。
    [string]$ReleaseVersion = "",
    [string]$Configuration = "Release",
    [switch]$SkipWebBuild,
    # 商店提交模式：不本地签名（交给 Partner Center），并提醒替换 Publisher。
    [switch]$ForStore,
    # 默认会组装 .msix-work\test-bundle\（可直接发到测试机的自包含目录）。
    # 只想拿包、不想多花十几秒解包复检时加这个。
    [switch]$SkipTestBundle
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
# 先记下系统临时目录，再把它改指到工作目录。
# 下面解包复检要写一个临时 .zip，那个必须落在**仓库外**——本机对仓库内文件删除会挂起。
$sysTemp = [System.IO.Path]::GetTempPath()
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

# 用**哈希表** splatting，不要用数组。
# 数组 splatting 不会把 "-Name" 当成参数名，而是当位置参数塞进去：
#   @('-Path','C:\','-Filter','*.txt')  →  '-Filter' 被当成 Path 的值
# 这里以前就是数组写法，于是 New-DutyAgentClientRelease.ps1 收到的是
#   OutputRoot='-OutputRoot' / Configuration=<staging 路径> / ReleaseVersion='-Configuration'
# 直接抛 "ReleaseVersion '-Configuration' is not a valid x.y.z[.w] version."
# （这段从 7bfb891 建工程起就是错的，等于发布步骤从来没按预期跑通过。）
$releaseArgs = @{
    OutputRoot    = $stagingRoot
    Configuration = $Configuration
    NoZip         = $true
}
if ($ReleaseVersion) { $releaseArgs['ReleaseVersion'] = $ReleaseVersion }
if ($SkipWebBuild) { $releaseArgs['SkipWebBuild'] = $true }

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
$arch = $preIdentity.GetAttribute("ProcessorArchitecture")
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
# winapp 把产物写到**当前工作目录**，不是 --manifest 所在目录。
# 不显式切目录的话，直接跑 build_msix.bat 会把 108MB 的 .msix 丢在仓库根
# （实测就是这么发生的：包出现在 D:\projects\D\Duty-Agent\ 而不是 .msix-work\）。
Push-Location $workRoot
try {
    & $winappCmd @packageArgs -v
    if ($LASTEXITCODE -ne 0) { throw "winapp package 失败，exit=$LASTEXITCODE" }
} finally {
    Pop-Location
}

# winapp 按 <Name>_<Version>_<Arch>.msix 命名，直接算出来。
# 别用"目录里最新的那个"：换过包身份之后 .msix-work 下会同时存在新旧两代产物，
# 靠时间排序迟早拿错（实测现在就躺着两个 DutyAgent_1.0.0.0_x64.msix）。
$expectedMsix = Join-Path $workRoot ("{0}_{1}_{2}.msix" -f $identityName, $msixVersion, $arch)

# 兜底：万一某个 winapp 版本仍把产物丢在调用方的 CWD，这里把它搬进工作目录，
# 而不是让后面的测试包组装找不到文件。
if (-not (Test-Path -LiteralPath $expectedMsix)) {
    $strayMsix = Join-Path (Get-Location).Path (Split-Path -Leaf $expectedMsix)
    if (Test-Path -LiteralPath $strayMsix) {
        Write-Host ("[msix] 产物落在当前目录，搬到工作目录：{0}" -f (Split-Path -Leaf $expectedMsix))
        Move-Item -LiteralPath $strayMsix -Destination $expectedMsix -Force
    }
}

if (-not (Test-Path -LiteralPath $expectedMsix)) {
    $present = @(Get-ChildItem -LiteralPath $workRoot -Filter "*.msix" -File -ErrorAction SilentlyContinue |
        ForEach-Object { $_.Name }) -join ", "
    throw "打包报告成功但没找到预期产物：$expectedMsix`n工作目录里现有的 .msix：$present"
}
Write-Host "[msix] 产物: $expectedMsix"

# 旧产物挪进 trash，不删：本机对仓库内文件删除会挂起。
$trashDir = Join-Path $workRoot "trash"
New-Item -ItemType Directory -Force -Path $trashDir | Out-Null
$stamp = Get-Date -Format "yyyyMMddHHmmss"
foreach ($f in @(Get-ChildItem -LiteralPath $workRoot -Filter "*.msix" -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -ne $expectedMsix })) {
    $dest = Join-Path $trashDir ("{0}.stale-{1}.msix" -f $f.BaseName, $stamp)
    Move-Item -LiteralPath $f.FullName -Destination $dest -Force
    Write-Host ("[msix] 旧产物已挪走：{0}" -f $f.Name)
}

# ---------- 6. 组装测试包 ----------
# 目标是"一个目录直接发到测试机"，所以连安装/卸载脚本和清单一起放进去。
# -ForStore 的包没签名，侧载装不上，不生成测试包。
$bundleDir = Join-Path $workRoot "test-bundle"
if ($ForStore) {
    Write-Host ""
    Write-Host "[msix] ForStore 模式：包未签名，侧载装不上，跳过测试包组装。"
} elseif ($SkipTestBundle) {
    Write-Host ""
    Write-Host "[msix] -SkipTestBundle：跳过测试包组装。"
} else {
    Write-Host ""
    Write-Host "[msix] 组装测试包：$bundleDir"

    # 先把包体解出来跑一次静态认证 —— 发出去的包必须是能装的。
    # 解包目录复用前先挪走（不删）；临时 zip 落在仓库外。
    $unpackDir = Join-Path $workRoot "test-bundle-unpack"
    if (Test-Path -LiteralPath $unpackDir) {
        Move-Item -LiteralPath $unpackDir -Destination (Join-Path $trashDir "test-bundle-unpack.$stamp") -Force
    }
    New-Item -ItemType Directory -Force -Path $unpackDir | Out-Null

    $tempZip = Join-Path $sysTemp ("duty-msix-verify-{0}.zip" -f $stamp)
    Copy-Item -LiteralPath $expectedMsix -Destination $tempZip -Force
    try {
        Expand-Archive -LiteralPath $tempZip -DestinationPath $unpackDir -Force
    } finally {
        Remove-Item -LiteralPath $tempZip -Force -ErrorAction SilentlyContinue
    }

    $packagedManifest = Join-Path $unpackDir "AppxManifest.xml"
    if (-not (Test-Path -LiteralPath $packagedManifest)) { throw "包内没有 AppxManifest.xml：$expectedMsix" }

    # 认证脚本失败时走 exit 1，直接 & 调用会把当前会话一起退掉，
    # 所以放到独立 powershell 进程里跑，只看退出码。
    # VersionSource 传绝对路径：构建脚本的 CWD 可能是任意目录，相对路径会找不到。
    $certScript = Join-Path $PSScriptRoot "MSIX-CertificationTest.ps1"
    & powershell -NoProfile -ExecutionPolicy Bypass -File $certScript `
        -ManifestPath $packagedManifest -LayoutPath $unpackDir `
        -VersionSource (Join-Path $root "Assets_Duty\version.py")
    if ($LASTEXITCODE -ne 0) {
        throw "认证检查未通过（见上面的 FAIL 项），未生成测试包。"
    }

    New-Item -ItemType Directory -Force -Path $bundleDir | Out-Null

    # 清掉测试包里的旧 msix（挪走，不删）
    foreach ($f in @(Get-ChildItem -LiteralPath $bundleDir -Filter "*.msix" -File -ErrorAction SilentlyContinue)) {
        Move-Item -LiteralPath $f.FullName -Destination (Join-Path $trashDir ("{0}.stale-{1}.msix" -f $f.BaseName, $stamp)) -Force
    }

    Copy-Item -LiteralPath $expectedMsix -Destination $bundleDir -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Import-DevCertificate.ps1") -Destination $bundleDir -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Install-MsixForTesting.ps1") -Destination $bundleDir -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Uninstall-MsixForTesting.ps1") -Destination $bundleDir -Force
    Copy-Item -LiteralPath (Join-Path $root "docs\msix-test-checklist.md") -Destination $bundleDir -Force

    # 双击入口（信任证书 / 安装 / 启动 / 卸载）。测试机上不该要求人先懂
    # Set-ExecutionPolicy 和 AUMID 长什么样；.ps1 留作实现，.bat 是壳。
    # 四个 .bat 内容一律 ASCII + CRLF（cmd 按 OEM 代码页渲染，中文会乱码），
    # 中文提示全部由 .ps1 输出（它们带 UTF-8 BOM）。
    # 0-装证书 是唯一需要管理员的步骤；信任之后 .msix 可以可以直接双击安装，
    # 对"没有管理员权限的测试员"这条链路是必需的。
    foreach ($batName in @("0-装证书.bat", "1-安装.bat", "2-启动.bat", "3-卸载.bat")) {
        $batSource = Join-Path $PSScriptRoot $batName
        if (Test-Path -LiteralPath $batSource) {
            Copy-Item -LiteralPath $batSource -Destination $bundleDir -Force
        } else {
            Write-Warning "缺少双击入口脚本：$batSource"
        }
    }

    # 桥接插件再单独带一份。MSIX 布局里本来就有一份（<安装目录>\bridge\，
    # 客户端界面的"安装 ClassIsland 插件"就是从那儿拷的），但便携版 ClassIsland、
    # 或者客户端自动发现配置目录失败时，得能手工拷进 <ClassIsland>\data\Plugins\。
    # 注意：不要用 Duty-Agent-ClassIsland-Bridge\cipx\*.cipx —— 那是 2026-09-12
    # 的旧构建（DLL 136KB vs 当前 166KB），装了会得到一个落后多代的插件。
    $bridgeSource = Join-Path $release.FullName "bridge"
    if (Test-Path -LiteralPath $bridgeSource) {
        $bridgeTarget = Join-Path $bundleDir "bridge"
        if (Test-Path -LiteralPath $bridgeTarget) {
            Move-Item -LiteralPath $bridgeTarget -Destination (Join-Path $trashDir "bundle-bridge.$stamp") -Force
        }
        Copy-Item -LiteralPath $bridgeSource -Destination $bundleDir -Recurse -Force
    } else {
        Write-Warning "发布产物里没有 bridge 目录（$bridgeSource），测试包将不含桥接插件。"
    }

    $certSubject = $null
    $certThumbprint = $null
    if ((Test-Path -LiteralPath $devCer)) {
        Copy-Item -LiteralPath $devCer -Destination $bundleDir -Force
        $x509 = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($devCer)
        $certSubject = $x509.Subject
        $certThumbprint = $x509.Thumbprint
    } else {
        Write-Warning "没找到 devcert.cer（$devCer），测试包里将没有证书，目标机装不上。"
    }

    $buildInfo = [ordered]@{
        builtAt      = (Get-Date).ToString("o")
        name         = $identityName
        publisher    = $publisher
        version      = $msixVersion
        architecture = $arch
        msix         = (Split-Path -Leaf $expectedMsix)
        certificate  = [ordered]@{ subject = $certSubject; thumbprint = $certThumbprint }
    }
    $buildInfo | ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath (Join-Path $bundleDir "build-info.json") -Encoding UTF8

    Write-Host "[msix] 测试包已就绪（可直接压缩发走）："
    foreach ($f in @(Get-ChildItem -LiteralPath $bundleDir -File | Sort-Object Name)) {
        Write-Host ("         {0,-40} {1,10:N1} MB" -f $f.Name, ($f.Length / 1MB))
    }
}

Write-Host ""
Write-Host "============================================================"
Write-Host " MSIX build done"
Write-Host (" 包身份 : {0} {1} ({2})" -f $identityName, $msixVersion, $arch)
Write-Host (" 产物   : {0}" -f $expectedMsix)
if (-not $ForStore -and -not $SkipTestBundle) {
    Write-Host (" 测试包 : {0}" -f $bundleDir)
    Write-Host " 目标机：管理员 PowerShell 跑 Install-MsixForTesting.ps1"
}
if (-not $ForStore) {
    Write-Host " 免安装调试：winapp create-debug-identity $pkgDir\duty-agent.exe --manifest $stagedManifest"
    Write-Host " 然后 shell:AppsFolder\<PackageFamilyName>!App 启动。"
}
Write-Host "============================================================"
