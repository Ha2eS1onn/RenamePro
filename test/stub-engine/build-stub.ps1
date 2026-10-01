# ---------------------------------------------------------------------------
# 把桩引擎发布成「一个可执行文件」并复制成 soffice.exe / soffice.com。
#
# 为什么必须单文件发布：调用方要把可执行文件改名成 soffice.exe 放进测试目录。
# 普通 dotnet build 出来的 stub-engine.exe 是 apphost，会去找同目录的 stub-engine.dll，
# 单独改名拷贝（只带一个 exe）会以「找不到 soffice.dll」启动失败；
# PublishSingleFile 把托管程序集打包进 exe 本体，改名后照常运行。
#
# 用法（与仓库其它脚本一致；本机执行策略禁止直接运行 .ps1，必须显式 Bypass）：
#   powershell -ExecutionPolicy Bypass -File test\stub-engine\build-stub.ps1
#   powershell -ExecutionPolicy Bypass -File test\stub-engine\build-stub.ps1 -OutDir C:\temp\engine
#
# 本文件必须保存为 UTF-8 **带 BOM**：Windows PowerShell 5.1 会把无 BOM 的 UTF-8 当成 ANSI(GBK)
# 读取，中文注释与提示会变成乱码甚至解析失败（仓库其它 .ps1 也都是带 BOM 的）。
#
# 产物（默认 -OutDir 为脚本同目录下的 dist\）：
#   dist\soffice.exe      改名后的桩（供调用方探测/调用）
#   dist\soffice.com      Windows 上 .com 与 .exe 一样是可执行 PE，用来验证两条探测路径
#   dist\stub-engine.exe  原始名（排障用）
# ---------------------------------------------------------------------------
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'

# 注意：$PSScriptRoot 在 param() 默认值里不保证有值（-File 调用时实测为空），所以在函数体里取。
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Definition }
if ([string]::IsNullOrWhiteSpace($OutDir)) { $OutDir = Join-Path $scriptDir 'dist' }

$project = Join-Path $scriptDir 'stub-engine.csproj'
if (-not (Test-Path -LiteralPath $project)) { throw "找不到项目文件：$project" }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path -LiteralPath $OutDir).Path

Write-Host "[stub-engine] publish -> $OutDir"
# --self-contained false：依赖本机已装的 .NET 8+ 共享运行时（配合 csproj 的 RollForward=LatestMajor）
dotnet publish $project `
    -c $Configuration `
    -r win-x64 `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:DebugType=embedded `
    -o $OutDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）" }

# 改名：同名 .com 也复制一份，测试两条探测路径（soffice.exe / soffice.com）
Copy-Item -LiteralPath (Join-Path $OutDir 'stub-engine.exe') -Destination (Join-Path $OutDir 'soffice.exe') -Force
Copy-Item -LiteralPath (Join-Path $OutDir 'stub-engine.exe') -Destination (Join-Path $OutDir 'soffice.com') -Force

# 自检：改名后的 exe 必须还能独立运行（证明它是真的单文件，不依赖同目录 dll）
$version = & (Join-Path $OutDir 'soffice.exe') --version
$versionExit = $LASTEXITCODE
if ($versionExit -ne 0) { throw "soffice.exe --version 退出码 $versionExit" }
Write-Host "[stub-engine] ok: soffice.exe --version -> $version"
Write-Host "[stub-engine] 可执行文件：$OutDir\soffice.exe （同目录 soffice.com 亦可）"
