# RenamePro 文档引擎（LibreOffice）开发环境准备脚本
# 用法：powershell -ExecutionPolicy Bypass -File setup-lo-dev.ps1 [-Version 25.8.7] [-TargetDir D:\RenamePro-dev\libreoffice] [-Force]
#
# 作用：下载官方 MSI → 校验官方 SHA256 → 用 msiexec /a 做「administrative install」把文件解出来，
#       不写 Program Files、不注册 COM、不改动系统。解出来的树就是 build-docengine.ps1 的输入（它负责裁剪成
#       随包的 libreoffice\ 目录），也可以在开发期直接把 documentEngine 指向它联调。
#
# 为什么用 msiexec /a 而不是装一遍 LibreOffice：
#   1) 我们只需要文件，不需要系统集成；装一遍反而要求管理员权限并留下卸载记录；
#   2) administrative install 是纯展开操作，可在任意目录复现，适合脚本化；
#   3) 产物不入库（.gitignore 已忽略），随时可删。
#
# 产物：<TargetDir>\program\soffice.com 等，仓库之外，不入库。
param(
    [string]$Version = '25.8.7',
    [string]$TargetDir = 'D:\RenamePro-dev\libreoffice',
    # MSI 缓存目录（默认放在仓库的 obj\ 下，已被 .gitignore 忽略）
    [string]$MsiDir = '',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $MsiDir) { $MsiDir = Join-Path $root 'obj\lo-msi' }

$base = "https://download.documentfoundation.org/libreoffice/stable/$Version/win/x86_64"
$msiName = "LibreOffice_${Version}_Win_x86-64.msi"
$msiPath = Join-Path $MsiDir $msiName
$shaPath = "$msiPath.sha256"
$soffice = Join-Path $TargetDir 'program\soffice.com'

Write-Host "=== RenamePro 文档引擎开发环境（LibreOffice $Version）===" -ForegroundColor Cyan
Write-Host "下载源  : $base"
Write-Host "MSI 缓存: $msiPath"
Write-Host "展开到  : $TargetDir"

# 1) 下载 MSI 与官方 sha256（已存在且能对上就跳过）
New-Item -ItemType Directory -Force -Path $MsiDir, $TargetDir | Out-Null

function Get-RemoteFile {
    param([string]$Url, [string]$Destination)
    $ProgressPreference = 'SilentlyContinue'
    $webClient = New-Object System.Net.WebClient
    try {
        $webClient.DownloadFile($Url, $Destination)
    } finally {
        $webClient.Dispose()
    }
}

if ($Force -or -not (Test-Path $msiPath)) {
    Write-Host "`n[1/4] 下载 MSI（约 349 MB，必要时请自备代理）…" -ForegroundColor Cyan
    Get-RemoteFile -Url "$base/$msiName" -Destination $msiPath
} else {
    Write-Host "`n[1/4] MSI 已存在，跳过下载" -ForegroundColor Cyan
}
if (-not (Test-Path $shaPath)) {
    Get-RemoteFile -Url "$base/$msiName.sha256" -Destination $shaPath
}

# 2) 校验官方 SHA256：下载被截断/被投毒都必须在这里失败
Write-Host "`n[2/4] 校验官方 SHA256…" -ForegroundColor Cyan
$expected = ((Get-Content $shaPath -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
$actual = (Get-FileHash $msiPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($expected -ne $actual) {
    Write-Host "  期望: $expected" -ForegroundColor Red
    Write-Host "  实际: $actual" -ForegroundColor Red
    throw "MSI 校验失败：文件损坏或被替换，请删除 $msiPath 后重试"
}
Write-Host "  OK $actual" -ForegroundColor Green

# 3) administrative install 展开（纯文件展开，不安装）
if ($Force -or -not (Test-Path $soffice)) {
    Write-Host "`n[3/4] 展开 MSI（msiexec /a，约 1~2 分钟）…" -ForegroundColor Cyan
    $log = Join-Path $MsiDir 'extract.log'
    $p = Start-Process -FilePath 'msiexec.exe' -Wait -PassThru -NoNewWindow -ArgumentList @(
        '/a', "`"$msiPath`"", '/qn', "TARGETDIR=`"$TargetDir`"", '/l*v', "`"$log`""
    )
    if ($p.ExitCode -ne 0) {
        Write-Host "  msiexec 退出码 $($p.ExitCode)，日志：$log" -ForegroundColor Red
        throw 'MSI 展开失败'
    }
    Write-Host '  展开完成' -ForegroundColor Green
} else {
    Write-Host "`n[3/4] 目标目录已存在 soffice.com，跳过展开（-Force 可强制重来）" -ForegroundColor Cyan
}

# 4) 探针：能跑起来才算可用（缺 VC++ 运行时 / 被杀软拦截会在这里暴露）
Write-Host "`n[4/4] 探针 soffice --version…" -ForegroundColor Cyan
if (-not (Test-Path $soffice)) {
    Write-Host "  未找到 $soffice" -ForegroundColor Red
    throw '展开结果里没有 soffice.com'
}
$version = & $soffice '--headless' '--version' 2>&1 | Select-Object -First 1
if (-not ($version -match 'LibreOffice')) {
    Write-Host "  输出异常：$version" -ForegroundColor Red
    Write-Host '  常见原因：缺少 VC++ 2015-2022 x64 运行时（msvcp140.dll / vcruntime140.dll）' -ForegroundColor Yellow
    throw 'soffice 探针失败'
}
Write-Host "  OK $version" -ForegroundColor Green

$size = (Get-ChildItem $TargetDir -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ''
Write-Host '=== 完成 ===' -ForegroundColor Cyan
Write-Host ("  引擎      : {0}" -f $soffice)
Write-Host ("  版本      : {0}" -f $version)
Write-Host ("  展开后体积: {0:N0} MB（{1} 个文件）" -f ($size / 1MB), (Get-ChildItem $TargetDir -Recurse -File).Count)
Write-Host ''
Write-Host '下一步：' -ForegroundColor Cyan
Write-Host '  1) 直接联调：把程序目录 config.json 的 documentEngine 设为 "libreoffice"，并设置环境变量'
Write-Host "       `$env:RENAMEPRO_SOFFICE = '$soffice'"
Write-Host '  2) 生成随包引擎目录（发布用）：powershell -ExecutionPolicy Bypass -File build-docengine.ps1'
Write-Host '       —— 会裁剪这棵树并复制到 libreoffice\，再由 publish.ps1 -Mode Docs 放进发布包'
