#Requires -RunAsAdministrator
<#
    .SYNOPSIS
        Duty-Agent MSIX 测试包 —— 安装（侧载，需管理员）。
    .DESCRIPTION
        给"小范围其他设备测试"用：信任开发证书公钥 → 安装 MSIX → 打印 PFN/AUMID。

        刻意**不硬编码**任何包身份：
          * Name / Publisher / Version 从 .msix 包内的 AppxManifest.xml 读；
          * 证书指纹从 devcert.cer 读；
          * 安装结果用 Get-AppxPackage 反查（Add-AppxPackage 无 -PassThru）。
        这样换 Publisher 或改 Name 之后，这个脚本不用改。

        装之前会校验证书 subject 是否等于清单 Identity.Publisher —— 不一致就报错，
        而不是让 Add-AppxPackage 吐一个看不懂的 0x800B0109。
    .PARAMETER MsixPath
        待装 MSIX。不给则在脚本所在目录找最新的 *.msix。
    .PARAMETER CertPath
        开发证书公钥 .cer。不给则用脚本所在目录的 devcert.cer。
    .PARAMETER SkipWebView2Check
        跳过 WebView2 运行时检测（只影响提示，不阻断安装）。
    .EXAMPLE
        Set-ExecutionPolicy -Scope Process Bypass -Force
        .\Install-MsixForTesting.ps1
#>
[CmdletBinding()]
param(
    [string]$MsixPath = "",
    [string]$CertPath = "",
    [switch]$SkipWebView2Check
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

function Resolve-TestMsix {
    param([string]$Explicit)
    if ($Explicit) {
        if (-not (Test-Path -LiteralPath $Explicit)) { throw "MSIX 不存在：$Explicit" }
        return (Resolve-Path -LiteralPath $Explicit).Path
    }
    $found = @(Get-ChildItem -LiteralPath $root -Filter '*.msix' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending)
    if ($found.Count -eq 0) { throw "目录里没有 .msix：$root" }
    if ($found.Count -gt 1) {
        Write-Warning ("目录里有 {0} 个 .msix，取最新的：{1}" -f $found.Count, $found[0].Name)
    }
    return $found[0].FullName
}

function Resolve-TestCert {
    param([string]$Explicit)
    if ($Explicit) {
        if (-not (Test-Path -LiteralPath $Explicit)) { throw "证书不存在：$Explicit" }
        return (Resolve-Path -LiteralPath $Explicit).Path
    }
    $default = Join-Path $root 'devcert.cer'
    if (-not (Test-Path -LiteralPath $default)) { throw "找不到 devcert.cer：$default" }
    return $default
}

<#
    直接从 .msix 里读 AppxManifest.xml，不解包整个 107MB。
    MSIX 就是个 zip，用 ZipFile 打开取那一个 entry 即可。
#>
function Read-PackageIdentity {
    param([string]$Path)

    Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = @($zip.Entries | Where-Object { $_.FullName -eq 'AppxManifest.xml' })
        if ($entry.Count -eq 0) { throw "包内找不到 AppxManifest.xml：$Path" }
        $stream = $entry[0].Open()
        try {
            $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)
            try { [xml]$doc = $reader.ReadToEnd() } finally { $reader.Dispose() }
        } finally {
            $stream.Dispose()
        }
    } finally {
        $zip.Dispose()
    }

    $nsm = New-Object System.Xml.XmlNamespaceManager($doc.NameTable)
    $nsm.AddNamespace('x', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $identity = $doc.SelectSingleNode('/x:Package/x:Identity', $nsm)
    if ($null -eq $identity) { throw "包内清单缺少 <Identity>" }

    return [pscustomobject]@{
        Name      = $identity.GetAttribute('Name')
        Publisher = $identity.GetAttribute('Publisher')
        Version   = $identity.GetAttribute('Version')
        Arch      = $identity.GetAttribute('ProcessorArchitecture')
    }
}

function Read-CertificateInfo {
    param([string]$Path)
    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($Path)
    return [pscustomobject]@{
        Subject    = $cert.Subject
        Thumbprint = $cert.Thumbprint
        NotAfter   = $cert.NotAfter
    }
}

# ---------------------------------------------------------------- 解析输入
$msix = Resolve-TestMsix -Explicit $MsixPath
$cer = Resolve-TestCert -Explicit $CertPath

Write-Host ''
Write-Host '===== Duty-Agent MSIX 测试包：安装 =====' -ForegroundColor Cyan
Write-Host ("  MSIX : {0}" -f (Split-Path -Leaf $msix))
Write-Host ("  证书 : {0}" -f (Split-Path -Leaf $cer))
Write-Host ''

# ---------------------------------------------------------------- 前置检查
$os = [System.Environment]::OSVersion.Version
Write-Host ("系统版本 : Windows {0}.{1} (build {2})" -f $os.Major, $os.Minor, $os.Build)
if ($os.Build -lt 18362) {
    throw ("本包要求 Windows 10 build 18362（1903）或更高，当前 build {0}。" -f $os.Build)
}
if (-not [System.Environment]::Is64BitOperatingSystem) {
    throw '本包只支持 64 位 Windows。'
}

$identity = Read-PackageIdentity -Path $msix
Write-Host ("包身份   : {0} {1} ({2})" -f $identity.Name, $identity.Version, $identity.Arch)

$certInfo = Read-CertificateInfo -Path $cer
Write-Host ("证书     : {0}，到期 {1:yyyy-MM-dd}" -f $certInfo.Subject, $certInfo.NotAfter)

# 关键前置校验：签名者 subject 必须等于清单 Publisher，否则装到一半才报 0x800B0109。
$normalize = { param($s) ($s -replace '\s', '') }
if ((& $normalize $certInfo.Subject) -ne (& $normalize $identity.Publisher)) {
    throw @"
证书与包身份不匹配，装不进去。
  证书 subject   : $($certInfo.Subject)
  清单 Publisher : $($identity.Publisher)
多半是拿了旧 Publisher 时代的 devcert.cer。重新跑一次
scripts\Build-MsixPackage.ps1 会用新 Publisher 自动重新生成证书并放进测试包。
"@
}

if (-not $SkipWebView2Check) {
    $wv2 = $null
    foreach ($p in @(
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
        'HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
    )) {
        $item = Get-ItemProperty -Path $p -ErrorAction SilentlyContinue
        if ($item -and $item.pv) { $wv2 = $item.pv; break }
    }
    if ($wv2) {
        Write-Host ("WebView2 : 已安装 {0}" -f $wv2) -ForegroundColor Green
    } else {
        Write-Host 'WebView2 : 未检测到。安装仍可继续，但主界面会提示「启动失败」。' -ForegroundColor Yellow
        Write-Host '           请先装运行时：https://go.microsoft.com/fwlink/p/?LinkId=2124703' -ForegroundColor Yellow
    }
}
Write-Host ''

# ---------------------------------------------------------------- 1/3 证书
Write-Host '[1/3] 导入开发证书到「本地计算机\受信任人」…'
$alreadyTrusted = @(Get-ChildItem Cert:\LocalMachine\TrustedPeople -ErrorAction SilentlyContinue |
    Where-Object { $_.Thumbprint -eq $certInfo.Thumbprint })
if ($alreadyTrusted.Count -gt 0) {
    Write-Host ("      已信任同指纹证书（{0}），跳过。" -f $certInfo.Thumbprint) -ForegroundColor Green
} else {
    Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
    Write-Host ("      完成（{0}）。" -f $certInfo.Thumbprint) -ForegroundColor Green
}

# ---------------------------------------------------------------- 2/3 安装
Write-Host '[2/3] 安装 MSIX（解包需要一点时间）…'
$newVersion = [version]$identity.Version
$existing = Get-AppxPackage -Name $identity.Name -ErrorAction SilentlyContinue
$forceUpdate = $false
if ($existing) {
    $existingVersion = $existing.Version
    if ($existingVersion -gt $newVersion) {
        throw @"
已安装版本高于待装版本，Windows 不允许"降级"安装。
  已安装 : $existingVersion
  待安装 : $newVersion
先跑 Uninstall-MsixForTesting.ps1 再装；或者把清单 Identity.Version 提高后重新出包。
（商店同样不接受版本回退，这个号一旦发出去就只能往上走。）
"@
    }
    if ($existingVersion -eq $newVersion) {
        Write-Host ("      检测到同版本已安装（{0}），加 -ForceUpdateFromAnyVersion 覆盖。" -f $existingVersion) -ForegroundColor Yellow
        $forceUpdate = $true
    } else {
        Write-Host ("      检测到旧版本 {0}，将升级到 {1}。" -f $existingVersion, $newVersion)
    }
}

$addArgs = @{ Path = $msix }
if ($forceUpdate) { $addArgs['ForceUpdateFromAnyVersion'] = $true }
# 不要用 -PassThru：Windows PowerShell 5.1 的 Add-AppxPackage 没有这个参数
# （实测 (Get-Command Add-AppxPackage).Parameters 里没有 PassThru），传了会直接
# ParameterBindingException 挂掉。装完用 Get-AppxPackage 查结果——和
# .github/workflows/msix.yml 里的安装步骤保持同一条路（CI 一直在用的写法）。
Add-AppxPackage @addArgs | Out-Null

$pkg = $null
foreach ($attempt in 1..3) {
    $pkg = Get-AppxPackage -Name $identity.Name -ErrorAction SilentlyContinue |
        Sort-Object Version -Descending |
        Select-Object -First 1
    if ($pkg) { break }
    Start-Sleep -Milliseconds 800
}
if (-not $pkg) {
    throw "安装命令已执行，但查不到包 $($identity.Name)。请重跑，或查看系统事件日志。"
}
Write-Host '      完成。' -ForegroundColor Green

# ---------------------------------------------------------------- 3/3 结果
Write-Host '[3/3] 安装结果：'
$pkg | Format-List Name, PackageFullName, Version, PackageFamilyName, InstallLocation

$pfn = $pkg.PackageFamilyName
Write-Host ''
Write-Host '安装成功。' -ForegroundColor Green
Write-Host ("  PFN   : {0}" -f $pfn)
Write-Host ("  AUMID : {0}!App" -f $pfn)
Write-Host  '  启动  : 开始菜单搜索「Duty-Agent」'
Write-Host ("  或    : explorer.exe shell:AppsFolder\{0}!App" -f $pfn)
Write-Host ''
Write-Host ("  自启值名 : DutyAgent.{0}  （带 PFN 作用域，与 zip 版的裸名 DutyAgent 分开）" -f $pfn)
Write-Host ''
Write-Host '接下来请对照同目录的 msix-test-checklist.md 逐项确认。'
Write-Host ''
