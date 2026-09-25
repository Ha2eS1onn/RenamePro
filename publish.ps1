# RenamePro 发布脚本（自包含 win-x64 + ReadyToRun；不使用 PublishTrim —— 会破坏 COM/反射）
# 用法：powershell -ExecutionPolicy Bypass -File publish.ps1 [-OutputDir <路径>]
param(
    [string]$OutputDir = (Join-Path $PSScriptRoot 'publish')
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'RenamePro.csproj'

Write-Host "=== RenamePro 发布开始 ===" -ForegroundColor Cyan
Write-Host "项目：$project"
Write-Host "输出：$OutputDir"

if (Test-Path $OutputDir) {
    Write-Host "清理已有输出目录…"
    Remove-Item $OutputDir -Recurse -Force
}

# 自包含 + ReadyToRun（不 Trim）
dotnet publish $project -c Release -r win-x64 --self-contained true /p:PublishReadyToRun=true -o $OutputDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "发布失败（退出码 $LASTEXITCODE）" -ForegroundColor Red
    exit $LASTEXITCODE
}

# 校验关键文件（config.json 属首次运行时自动生成的用户数据，不在此校验）
$required = @('RenamePro.exe', 'ffmpeg.exe', 'ffprobe.exe')
$missing = @()
foreach ($file in $required) {
    if (-not (Test-Path (Join-Path $OutputDir $file))) { $missing += $file }
}
if ($missing.Count -gt 0) {
    Write-Host ("警告：发布目录缺少文件：" + ($missing -join ', ')) -ForegroundColor Yellow
} else {
    Write-Host "关键文件校验通过（RenamePro.exe / ffmpeg.exe / ffprobe.exe）" -ForegroundColor Green
}

$size = (Get-ChildItem $OutputDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host ("发布完成：{0}（约 {1:N0} MB）" -f $OutputDir, $size) -ForegroundColor Green
Write-Host "提示：config.json 若未随发布生成，首次运行程序会自动创建（含中文注释）。"