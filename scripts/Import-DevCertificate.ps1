#Requires -RunAsAdministrator
<#
    .SYNOPSIS
        Duty-Agent MSIX 测试包 —— 信任开发证书（需管理员，每台机器一次）。
    .DESCRIPTION
        把 devcert.cer 导入「本地计算机\受信任人」，这是侧载 MSIX 唯一需要
        管理员的步骤。信任完之后，**装包本身不再需要管理员**：直接双击
        .msix 走 App Installer，或者跑 1-安装.bat 都可以。

        设计约束（测试包整个目录会被拷到任意路径）：
          * 不硬编码任何路径，证书默认就在本脚本旁边；
          * 幂等：已信任同指纹证书就跳过，可反复跑；
          * 身份信息全部从证书本身读，不依赖 build-info.json 是否存在。
    .PARAMETER Root
        测试包目录。不给则用本脚本所在目录。
    .PARAMETER CertPath
        证书公钥 .cer。不给则用 <Root>\devcert.cer。
    .EXAMPLE
        .\Import-DevCertificate.ps1
    .EXAMPLE
        由 0-装证书.bat 调用（自动带上 bundle 目录）：
        powershell -ExecutionPolicy Bypass -File Import-DevCertificate.ps1 -Root "E:\some\test-bundle\"
#>
[CmdletBinding()]
param(
    [string]$Root = "",
    [string]$CertPath = ""
)

$ErrorActionPreference = 'Stop'

if (-not $Root) { $Root = $PSScriptRoot }
if (-not $CertPath) { $CertPath = Join-Path $Root 'devcert.cer' }

# 防御：调用方从命令行传目录时，反斜杠+引号的组合会把一个引号留在参数里
# （0-装证书.bat 早期版本就踩过：-Root "%ROOT%" 里 %~dp0 带尾部反斜杠，
# PowerShell 把 \" 当转义引号，路径末尾多一个 "，之后每个 Test-Path 都报
# "路径中具有非法字符"）。这里统一剥掉包裹引号和尾部 separators。
function Normalize-BundlePath {
    param([string]$Value)
    if (-not $Value) { return $Value }
    return $Value.Trim().Trim('"', "'").TrimEnd('\', '/')
}
$Root = Normalize-BundlePath $Root
if (-not $CertPath -or $CertPath -eq (Join-Path $Root 'devcert.cer')) {
    $CertPath = Join-Path $Root 'devcert.cer'
}

function Resolve-CertFile {
    param([string]$Explicit, [string]$BundleRoot)
    if ($Explicit) {
        if (-not (Test-Path -LiteralPath $Explicit)) { throw "证书不存在：$Explicit" }
        return (Resolve-Path -LiteralPath $Explicit).Path
    }
    # 目录会被拷到任意位置，所以只在 bundle 自己里面找，并明确报出找过哪儿。
    $candidates = @(
        (Join-Path $BundleRoot 'devcert.cer'),
        (Join-Path $BundleRoot '*.cer')
    )
    foreach ($pattern in $candidates) {
        $hit = @(Get-ChildItem -Path $pattern -File -ErrorAction SilentlyContinue |
            Sort-Object Name)
        if ($hit.Count -gt 0) { return $hit[0].FullName }
    }
    throw "在测试包目录里找不到 .cer 证书（找过：$BundleRoot\devcert.cer）。`n请确认整个测试包目录都拷过来了，而不是只拷了 .msix。"
}

$cer = Resolve-CertFile -Explicit $CertPath -BundleRoot $Root

Write-Host ''
Write-Host '===== Duty-Agent MSIX 测试包：信任开发证书 =====' -ForegroundColor Cyan
Write-Host ("  目录 : {0}" -f $Root)
Write-Host ("  证书 : {0}" -f (Split-Path -Leaf $cer))
Write-Host ''

# ---------------------------------------------------------------- 读取证书
$cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($cer)
Write-Host ("证书主题 : {0}" -f $cert.Subject)
Write-Host ("指纹     : {0}" -f $cert.Thumbprint)
Write-Host ("有效期   : {0:yyyy-MM-dd} ~ {1:yyyy-MM-dd}" -f $cert.NotBefore, $cert.NotAfter)

if ($cert.NotAfter -lt (Get-Date)) {
    throw @"
证书已过期（$($cert.NotAfter.ToString('yyyy-MM-dd'))），装包会因签名失效被拒。
重新跑一次 scripts\Build-MsixPackage.ps1 会用新证书重新签名并放进测试包。
"@
}
$daysLeft = ($cert.NotAfter - (Get-Date)).Days
if ($daysLeft -lt 30) {
    Write-Host ("注意：证书还有 {0} 天过期，建议尽快重新出包。" -f $daysLeft) -ForegroundColor Yellow
}

# 私钥检查：侧载只需要公钥 .cer。万一有人把 .pfx 放进来了，明确拒绝并说清风险。
if ($cert.HasPrivateKey) {
    throw @"
这个文件带私钥（$(Split-Path -Leaf $cer)）。测试包只需要公钥 .cer；
把带私钥的证书发给别人等于把签名能力交出去。请改用 devcert.cer。
"@
}
Write-Host ''

# ---------------------------------------------------------------- 导入
$store = 'Cert:\LocalMachine\TrustedPeople'
$already = @(Get-ChildItem -Path $store -ErrorAction SilentlyContinue |
    Where-Object { $_.Thumbprint -eq $cert.Thumbprint })

if ($already.Count -gt 0) {
    Write-Host ('「本地计算机\受信任人」里已有同指纹证书，跳过导入（可反复运行）。') -ForegroundColor Green
}
else {
    Write-Host '导入到「本地计算机\受信任人」…'
    Import-Certificate -FilePath $cer -CertStoreLocation $store | Out-Null
    Write-Host '完成。' -ForegroundColor Green
}

# ---------------------------------------------------------------- 复核
$verify = @(Get-ChildItem -Path $store -ErrorAction SilentlyContinue |
    Where-Object { $_.Thumbprint -eq $cert.Thumbprint })
if ($verify.Count -eq 0) {
    throw "导入后仍在「受信任人」里查不到该证书（指纹 $($cert.Thumbprint)）。请用管理员身份重跑。"
}

Write-Host ''
Write-Host '证书已信任。' -ForegroundColor Green
Write-Host ''
Write-Host '接下来（不再需要管理员）：' -ForegroundColor Cyan
Write-Host '  方式一（推荐）：直接双击测试包里的 .msix，在 App Installer 窗口点「安装」'
Write-Host '  方式二：双击 1-安装.bat（会自动提权，但证书已信任时其实不需要）'
Write-Host ''
