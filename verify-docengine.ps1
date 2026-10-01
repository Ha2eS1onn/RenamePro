# RenamePro 文档转换端到端校验（真实 LibreOffice / 随包引擎目录 / 发布包目录包）
#
# 用法（三选一）：
#   powershell -ExecutionPolicy Bypass -File verify-docengine.ps1 -Soffice 'D:\RenamePro-dev\libreoffice\program\soffice.com'
#   powershell -ExecutionPolicy Bypass -File verify-docengine.ps1 -EngineDir .\libreoffice      # 验随包引擎目录（裁剪树）
#   powershell -ExecutionPolicy Bypass -File verify-docengine.ps1 -Package dist\RenamePro-全功能版-v1.2.0.zip
#       —— 解压发布包，用里面的 RenamePro.exe + 同级 LibreOffice\ 跑完整矩阵（最强的一种，验的正是用户拿到的形态）
#
# 做法：生成**真实**夹具（python-docx / python-pptx / openpyxl，含中文、表格、图片、多页、公式）
#       → 写测试用 config.json → 启动托盘程序 → 模拟"在资源管理器里改后缀" → 断言产物与日志。
# 覆盖 9 条成功路径、6 条失败/边界路径、1 条图片回归；打印 PASS/FAIL 表并以退出码表示成败。
#
# 为什么不用桩引擎：桩只能验集成链路（参数拼接、产物找回、原子替换），验不了"转换质量"。
# 真实引擎才能回答"中文掉不掉字形、表格页数对不对"。
param(
    [string]$Soffice = '',
    [string]$EngineDir = '',
    [string]$Package = '',
    [string]$Fixtures = '',
    [switch]$KeepRunning,
    [int]$StepTimeout = 180,
    [string]$ExePath = ''
)

$ErrorActionPreference = 'Stop'
$script:failed = 0
$script:passed = 0

function Assert-True {
    param([string]$Name, [bool]$Condition, [string]$Detail = '')
    if ($Condition) {
        Write-Host ("  [PASS] {0}" -f $Name) -ForegroundColor Green
        $script:passed++
    } else {
        $suffix = if ($Detail) { " | $Detail" } else { '' }
        Write-Host ("  [FAIL] {0}{1}" -f $Name, $suffix) -ForegroundColor Red
        $script:failed++
    }
}

function Write-Step { param([string]$Text); Write-Host ''; Write-Host ("=== {0} ===" -f $Text) -ForegroundColor Cyan }

# ---------------------------------------------------------------- 路径与前提

$root = $PSScriptRoot
if (-not $Soffice -and -not $EngineDir -and -not $Package) {
    throw '请指定一种模式：-Soffice <soffice.com>（真实引擎直连）| -EngineDir <随包引擎目录> | -Package <发布包 zip 或已解压目录>'
}

# -Package：先把发布包解压出来，用里面的 exe 与同级 LibreOffice\ 跑（用户拿到的就是这个形态）
if ($Package) {
    $packageRoot = Join-Path $root 'obj\dist-pkg'
    if (Test-Path -LiteralPath $Package -PathType Container) {
        $packageRoot = (Resolve-Path -LiteralPath $Package).Path
        Write-Host ("使用已解压的发布包目录：{0}" -f $packageRoot) -ForegroundColor Cyan
    } else {
        if (-not (Test-Path -LiteralPath $Package)) { throw "找不到发布包：$Package" }
        Write-Host ("解压发布包（约 700 MB，请稍等）：{0}" -f $Package) -ForegroundColor Cyan
        if (Test-Path $packageRoot) { Remove-Item $packageRoot -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
        Expand-Archive -LiteralPath $Package -DestinationPath $packageRoot -Force
    }
    $packagedExe = Get-ChildItem $packageRoot -Recurse -Filter 'RenamePro.exe' -File -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $packagedExe) { throw "发布包里找不到 RenamePro.exe：$packageRoot" }
    $ExePath = $packagedExe.FullName
}

if (-not $ExePath) { $ExePath = Join-Path $root 'bin\Release\net8.0-windows10.0.19041.0\win-x64\RenamePro.exe' }
if (-not (Test-Path $ExePath)) { throw "未找到 $ExePath，请先 dotnet build -c Release" }
$appDir = Split-Path $ExePath -Parent
$logPath = Join-Path $appDir 'log.txt'
# 夹具默认放仓库 obj\ 下：不污染用户目录，且在"只允许写工作区"的受限环境里也能跑
if (-not $Fixtures) { $Fixtures = Join-Path $root 'obj\verify-fixtures' }

# 引擎路由与"期望的引擎来源"（写日志里的 origin，用于断言确实走了想验的那条路）
$expectOrigin = ''
if ($Soffice) {
    if (-not (Test-Path $Soffice)) { throw "找不到引擎：$Soffice" }
    $ver = (& $Soffice '--headless' '--version' 2>&1 | Select-Object -First 1)
    if (-not ($ver -match 'LibreOffice')) { throw "引擎探针失败：$ver" }
    Write-Host ("使用真实引擎（环境变量直连）：{0}" -f $ver) -ForegroundColor Cyan
    $expectOrigin = '环境变量 RENAMEPRO_SOFFICE'
} elseif ($EngineDir) {
    $engineCom = Join-Path $EngineDir 'program\soffice.com'
    if (-not (Test-Path $engineCom)) { throw "随包引擎目录里没有 program\soffice.com：$EngineDir" }
    $engineMb = (Get-ChildItem $EngineDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
    Write-Host ("使用随包引擎目录：{0}（{1:N0} MB）" -f $engineCom, $engineMb) -ForegroundColor Cyan
    $expectOrigin = '随包目录'
} else {
    $engineCom = Join-Path $appDir 'LibreOffice\program\soffice.com'
    if (-not (Test-Path $engineCom)) {
        throw "全功能版目录包里没有 LibreOffice\program\soffice.com：$appDir（发布包结构不对？）"
    }
    $engineMb = (Get-ChildItem (Join-Path $appDir 'LibreOffice') -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
    Write-Host ("使用发布包自带的引擎目录：{0}（{1:N0} MB）" -f $engineCom, $engineMb) -ForegroundColor Cyan
    $expectOrigin = '随包目录'
}
Write-Host "程序：$ExePath"

# ---------------------------------------------------------------- 夹具

Write-Step '1) 生成真实夹具'
$python = 'D:\DeepSeek_Harness\resources\runtime\primary-runtime\dependencies\python\python.exe'
if (-not (Test-Path $python)) {
    $cmd = Get-Command python.exe -ErrorAction SilentlyContinue
    if (-not $cmd) { throw '找不到 Python（生成夹具需要 python-docx / python-pptx / openpyxl / Pillow）' }
    $python = $cmd.Source
}
if (Test-Path $Fixtures) { Remove-Item $Fixtures -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Fixtures | Out-Null

$makerPath = Join-Path $Fixtures 'make-fixtures.py'
@'
import os, sys
from docx import Document
from docx.shared import Inches
from pptx import Presentation
from openpyxl import Workbook
from PIL import Image

out = sys.argv[1]
img_path = os.path.join(out, 'fig.png')
Image.new('RGB', (320, 200), (60, 120, 200)).save(img_path)

d = Document()
d.add_heading('RenamePro 文档转换测试报告', 0)
d.add_paragraph('本文件用于验证 docx to pdf 的真实转换效果，包含中文、表格与图片。')
p = d.add_paragraph()
p.add_run('这一段是粗体，').bold = True
p.add_run('这一段是斜体。').italic = True
t = d.add_table(rows=3, cols=3)
for r in range(3):
    for c in range(3):
        t.cell(r, c).text = 'cell %d-%d' % (r + 1, c + 1)
d.add_picture(img_path, width=Inches(3))
d.save(os.path.join(out, 'report.docx'))

prs = Presentation()
s1 = prs.slides.add_slide(prs.slide_layouts[0])
s1.shapes.title.text = 'RenamePro slides'
s1.placeholders[1].text = 'verify pptx to pdf'
s2 = prs.slides.add_slide(prs.slide_layouts[1])
s2.shapes.title.text = 'page two'
s2.placeholders[1].text = 'alpha\nbeta\ngamma'
s3 = prs.slides.add_slide(prs.slide_layouts[5])
s3.shapes.title.text = 'page three'
prs.save(os.path.join(out, 'slides.pptx'))

wb = Workbook()
ws = wb.active
ws.title = 'data'
ws['A1'] = 'item'; ws['B1'] = 'qty'
for i, (name, n) in enumerate([('apple', 12), ('banana', 30), ('orange', 7)], start=2):
    ws['A%d' % i] = name; ws['B%d' % i] = n
ws['B5'] = '=SUM(B2:B4)'
ws2 = wb.create_sheet('notes')
ws2['A1'] = 'second sheet'
wb.save(os.path.join(out, 'sheet.xlsx'))

with open(os.path.join(out, 'notes.md'), 'w', encoding='utf-8') as f:
    f.write('# Markdown heading\n\nbody with **bold** and *italic*.\n\n'
            '- item one\n- item two\n\n```python\nprint("hi")\n```\n\n'
            '| A | B |\n| --- | --- |\n| x | y |\n\n[link](https://example.com)\n')

with open(os.path.join(out, 'page.html'), 'w', encoding='utf-8') as f:
    f.write('<!DOCTYPE html><html><head><meta charset="utf-8"><title>t</title></head>'
            '<body><h1>HTML heading</h1><p>paragraph</p></body></html>')

Image.new('RGB', (64, 64), (200, 60, 60)).save(os.path.join(out, 'real.png'))
Image.new('RGB', (64, 64), (60, 200, 60)).save(os.path.join(out, 'photo.jpg'))
print('fixtures ready')
'@ | Set-Content -LiteralPath $makerPath -Encoding UTF8
& $python $makerPath $Fixtures | ForEach-Object { Write-Host "  $_" }

# 夹具清单（固定英文名：PowerShell 5.1 会把中文参数按 GBK 传参，避免踩这个坑）
$names = @{
    docx = 'report.docx'
    pptx = 'slides.pptx'
    xlsx = 'sheet.xlsx'
    md   = 'notes.md'
    html = 'page.html'
    png  = 'real.png'
    jpg  = 'photo.jpg'
}
$originals = Join-Path $Fixtures 'originals'
New-Item -ItemType Directory -Force -Path $originals | Out-Null
foreach ($n in $names.Values) { Copy-Item (Join-Path $Fixtures $n) $originals -Force }
$fixtureCount = @(Get-ChildItem $originals -File).Count
Assert-True '夹具生成成功（docx/pptx/xlsx/md/html/png/jpg）' ($fixtureCount -ge 7) "实际 $fixtureCount 个"

# ---------------------------------------------------------------- 配置与启动

Write-Step '2) 写测试配置并启动托盘程序'
$configPath = Join-Path $appDir 'config.json'
# -Soffice：强制走系统安装档（真正的路由由 RENAMEPRO_SOFFICE 覆盖决定）
# -EngineDir：强制 bundled（只认随包目录）
# -Package：auto（验的正是"随包目录优先"这条默认路径）
$engineMode = if ($Soffice) { 'libreoffice' } elseif ($EngineDir) { 'bundled' } else { 'auto' }
@"
{
  "enableBackup": true,
  "autoRollbackOnFailure": false,
  "enableToast": false,
  "showProgressDialog": false,
  "imageQuality": 92,
  "gifPolicy": "first-frame",
  "imageConcurrency": 2,
  "watchDrives": [],
  "skipCloudFiles": true,
  "documentConversion": true,
  "documentEngine": "$engineMode",
  "allowPdfSource": false,
  "docTimeoutSeconds": 300,
  "docMaxConcurrency": 1,
  "docComRetries": 3,
  "docWarmupOnStart": true
}
"@ | Set-Content -LiteralPath $configPath -Encoding UTF8
Remove-Item $logPath -Force -ErrorAction SilentlyContinue

Get-Process RenamePro -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 400
if ($Soffice) { $env:RENAMEPRO_SOFFICE = $Soffice }
if ($EngineDir) { $env:RENAMEPRO_DOCENGINE = (Resolve-Path -LiteralPath $EngineDir).Path }
# 把程序的工作根固定到仓库内：受限环境（只允许写工作区）下系统 %TEMP% / %LOCALAPPDATA% 可能被拒，
# 那样预热与 profile 初始化会失败。生产环境不设这个变量，走正常用户目录。
$env:RENAMEPRO_DOCROOT = Join-Path $root 'obj\doc-work'
New-Item -ItemType Directory -Force -Path $env:RENAMEPRO_DOCROOT | Out-Null
# 同理把 TMP/TEMP 也指到仓库内：单文件 exe 启动时要把 bundle 解压到 %TEMP%\.net\<程序>\<hash>\，
# 受限环境下这一步会直接以 "Failed to create default extraction directory … error code: 5" 失败，
# 表现为"托盘程序没启动、连 log.txt 都没有"。生产环境不设，走正常用户目录。
$env:TEMP = Join-Path $root 'obj\temp'
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null
Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', "`"$ExePath`"" -WorkingDirectory $appDir -WindowStyle Hidden
Start-Sleep -Seconds 4

function Get-LogText {
    if (-not (Test-Path $logPath)) { return '' }
    return (Get-Content $logPath -Raw -Encoding UTF8)
}

# 等"某个文件的转换结束"：必须匹配该文件自己的 [结果] 行。
# 只按文件名子串匹配会串台——等 report.docx 时会命中上一条 "report.odt → report.docx" 的结果行。
function Wait-Result {
    param([string]$LeafName, [int]$Since = 0, [int]$TimeoutSeconds = 0)
    if ($TimeoutSeconds -le 0) { $TimeoutSeconds = $StepTimeout }
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $text = Get-LogText
        if ($text.Length -gt $Since) {
            $tail = $text.Substring($Since)
            $hit = @(($tail -split "`r?`n") | Where-Object {
                if ($_ -notmatch '\[结果\]') { return $false }
                # 结果行的形状：… | 新: <完整路径> | …，按"新: "切出路径再比末尾文件名
                $parts = $_ -split '新: '
                if ($parts.Count -lt 2) { return $false }
                $path = ($parts[1] -split '\|')[0].Trim()
                return $path.EndsWith($LeafName)
            })
            if ($hit.Count -gt 0) { return ($hit | Select-Object -Last 1) }
        }
        Start-Sleep -Milliseconds 400
    }
    return $null
}

Assert-True '托盘程序已启动' ((Get-LogText) -match '程序已启动')
$engineReady = $false
$deadline = (Get-Date).AddSeconds(600)
while ((Get-Date) -lt $deadline) {
    if ((Get-LogText) -match '\[文档\] 引擎就绪') { $engineReady = $true; break }
    Start-Sleep -Milliseconds 500
}
Assert-True '文档引擎就绪' $engineReady
if (-not $engineReady) {
    Get-LogText | Select-String -Pattern '\[文档\]|\[载荷\]|WARN|ERROR' | Select-Object -Last 8 | ForEach-Object { Write-Host ("  " + $_.Line) -ForegroundColor Yellow }
}
# 引擎来源必须是本次想验的那条路（否则可能悄悄用了系统安装的 LibreOffice，验了个假的）
Assert-True ("引擎来源是「{0}」" -f $expectOrigin) ((Get-LogText) -match [regex]::Escape($expectOrigin)) `
    (("引擎就绪行：" + (Get-LogText -split "`r?`n" | Where-Object { $_ -match '引擎就绪' } | Select-Object -Last 1)))

# 随包目录版不应再出现"解压文档引擎载荷"的痕迹（1.2 起引擎就是普通目录）
if ($Package -or $EngineDir) {
    $runtimeDir = Join-Path $env:RENAMEPRO_DOCROOT 'runtime'
    $payloadDirs = @(Get-ChildItem $runtimeDir -Directory -Filter 'doc-*' -ErrorAction SilentlyContinue)
    Assert-True '没有解压文档引擎载荷（随包目录版不该有 runtime\doc-*）' ($payloadDirs.Count -eq 0) ($payloadDirs.Name -join ', ')
}

# ---------------------------------------------------------------- 断言工具

function Test-Magic {
    param([string]$Path, [string]$Expect)
    if (-not (Test-Path $Path)) { return $false }
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 8) { return $false }
    switch -Regex ($Expect) {
        '^%PDF-' { return ([System.Text.Encoding]::ASCII.GetString($bytes[0..4]) -eq '%PDF-') }
        '^PK:ooxml' {
            try {
                Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
                $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
                $entries = @($zip.Entries | ForEach-Object FullName)
                $zip.Dispose()
                return (($entries -contains '[Content_Types].xml') -and ($entries -contains 'word/document.xml'))
            } catch { return $false }
        }
        '^PK:odf' {
            try {
                Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
                $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
                $entries = @($zip.Entries | ForEach-Object FullName)
                $zip.Dispose()
                return ($entries -contains 'mimetype')
            } catch { return $false }
        }
        '^png' { return ($bytes[0] -eq 0x89 -and $bytes[1] -eq 0x50 -and $bytes[2] -eq 0x4E -and $bytes[3] -eq 0x47) }
        '^html' { return (([System.Text.Encoding]::UTF8.GetString($bytes)) -match '(?i)<html|<!doctype') }
        '^text' { return $true }
        default { return $true }
    }
}

function Invoke-Case {
    param(
        [string]$Name,
        [string]$SourceKey,
        [string]$TargetExt,
        [string]$ExpectMagic,
        [string]$ExpectResult = 'OK'
    )
    $work = Join-Path $Fixtures ("case-" + $Name)
    if (Test-Path $work) { Remove-Item $work -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    $sourceName = $names[$SourceKey]
    $base = [IO.Path]::GetFileNameWithoutExtension($sourceName)
    $src = Join-Path $work $sourceName
    $dst = Join-Path $work ($base + $TargetExt)
    Copy-Item (Join-Path $originals $sourceName) $src -Force

    $since = (Get-LogText).Length
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Rename-Item -LiteralPath $src -NewName ([IO.Path]::GetFileName($dst))
    $line = Wait-Result -LeafName ([IO.Path]::GetFileName($dst)) -Since $since
    $sw.Stop()

    $result = if ($line -and $line -match '\[结果\] (OK|FAILED|SKIPPED|CANCELLED)') { $Matches[1] } else { '(无结果行)' }
    $magicOk = $false; $sizeText = '缺失'
    if (Test-Path $dst) {
        $sizeText = "$((Get-Item $dst).Length) B"
        $magicOk = Test-Magic -Path $dst -Expect $ExpectMagic
    }
    $ok = ($result -eq $ExpectResult) -and (($ExpectResult -ne 'OK') -or $magicOk)
    $detail = "结果=$result 产物=$sizeText magic=$magicOk 耗时=$([math]::Round($sw.Elapsed.TotalSeconds,1))s"
    Assert-True ("{0}（{1} → {2}）" -f $Name, $sourceName, $TargetExt) $ok $detail
    if (-not $ok -and $line) { Write-Host ("      日志：" + $line.Trim()) -ForegroundColor DarkGray }
    return [pscustomobject]@{ Name = $Name; Result = $result; Target = $dst; MagicOk = $magicOk; Work = $work; Line = $line }
}

# ---------------------------------------------------------------- 3) 成功路径

Write-Step '3) 成功路径（真实转换 + 产物校验）'
$cases = @()
$cases += Invoke-Case -Name 'docx2pdf' -SourceKey docx -TargetExt '.pdf' -ExpectMagic '%PDF-'
$cases += Invoke-Case -Name 'pptx2pdf' -SourceKey pptx -TargetExt '.pdf' -ExpectMagic '%PDF-'
$cases += Invoke-Case -Name 'xlsx2pdf' -SourceKey xlsx -TargetExt '.pdf' -ExpectMagic '%PDF-'
$cases += Invoke-Case -Name 'xlsx2csv' -SourceKey xlsx -TargetExt '.csv' -ExpectMagic 'text'
$cases += Invoke-Case -Name 'docx2odt' -SourceKey docx -TargetExt '.odt' -ExpectMagic 'PK:odf'
$cases += Invoke-Case -Name 'html2pdf' -SourceKey html -TargetExt '.pdf' -ExpectMagic '%PDF-'
$cases += Invoke-Case -Name 'docx2html' -SourceKey docx -TargetExt '.html' -ExpectMagic 'html'
$cases += Invoke-Case -Name 'md2docx' -SourceKey md -TargetExt '.docx' -ExpectMagic 'PK:ooxml'

# odt → docx 反向（用上一步的产物当输入，覆盖导入侧过滤器）
$odtCase = $cases | Where-Object { $_.Name -eq 'docx2odt' } | Select-Object -First 1
$odtWork = Join-Path $Fixtures 'case-odt2docx'
New-Item -ItemType Directory -Force -Path $odtWork | Out-Null
if ($odtCase -and (Test-Path $odtCase.Target)) {
    $src = Join-Path $odtWork 'report.odt'
    Copy-Item $odtCase.Target $src -Force
    $since = (Get-LogText).Length
    Rename-Item -LiteralPath $src -NewName 'report.docx'
    $line = Wait-Result -LeafName 'report.docx' -Since $since
    $dst = Join-Path $odtWork 'report.docx'
    $result = if ($line -and $line -match '\[结果\] (OK|FAILED|SKIPPED|CANCELLED)') { $Matches[1] } else { '(无结果行)' }
    $magic = Test-Magic -Path $dst -Expect 'PK:ooxml'
    Assert-True 'odt2docx（odt → docx）' (($result -eq 'OK') -and $magic) "结果=$result magic=$magic"
} else {
    Assert-True 'odt2docx（odt → docx）' $false '前置 docx→odt 未成功，无法反向验证'
}

$backups = @(Get-ChildItem $Fixtures -Recurse -File | Where-Object { $_.Name -match '副本' })
Assert-True '按配置生成了原格式副本' ($backups.Count -ge 5) "找到 $($backups.Count) 个副本"

$pdf = ($cases | Where-Object { $_.Name -eq 'docx2pdf' } | Select-Object -First 1).Target
Assert-True 'docx→pdf 产物大小合理（>5KB）' ((Test-Path $pdf) -and ((Get-Item $pdf).Length -gt 5120))

# ---------------------------------------------------------------- 4) 失败与边界

Write-Step '4) 失败路径与边界'
# 跨族 docx → xlsx 必须被拒
$crossWork = Join-Path $Fixtures 'case-crossfamily'
New-Item -ItemType Directory -Force -Path $crossWork | Out-Null
Copy-Item (Join-Path $originals 'report.docx') (Join-Path $crossWork 'report.docx') -Force
$since = (Get-LogText).Length
Rename-Item (Join-Path $crossWork 'report.docx') 'report.xlsx'
$crossLine = Wait-Result -LeafName 'report.xlsx' -Since $since
$crossOk = ($crossLine -and $crossLine -match 'SKIPPED' -and $crossLine -match 'csv|pdf')
Assert-True 'docx → xlsx 被拒绝且原因可操作' $crossOk $(if ($crossLine) { $crossLine.Trim() } else { '无结果行' })

# 伪后缀：PNG 改名成 .docx 必须被拦下。
# 注意这里走的是"源扩展名根本不在文档管线内"这条分支（更准确的提示），不是 magic 分支。
$fakeWork = Join-Path $Fixtures 'case-fakeext'
New-Item -ItemType Directory -Force -Path $fakeWork | Out-Null
Copy-Item (Join-Path $originals 'real.png') (Join-Path $fakeWork 'fake.png') -Force
$since = (Get-LogText).Length
Rename-Item (Join-Path $fakeWork 'fake.png') 'fake.docx'
$fakeLine = Wait-Result -LeafName 'fake.docx' -Since $since
$fakeOk = ($fakeLine -and $fakeLine -match 'SKIPPED' -and $fakeLine -match '不在文档集合内')
Assert-True 'PNG 改名成 .docx 被拦下（源扩展名不在管线）' $fakeOk $(if ($fakeLine) { $fakeLine.Trim() } else { '无结果行' })
Assert-True '被拦下的文件仍是原始 PNG 字节' ((Get-FileHash (Join-Path $fakeWork 'fake.docx')).Hash -eq (Get-FileHash (Join-Path $originals 'real.png')).Hash)

# 真正的 magic 不符：.docx 后缀里装 pptx 内容（两者都在管线内，靠文件头识别出来）
$mismatchWork = Join-Path $Fixtures 'case-magicmismatch'
New-Item -ItemType Directory -Force -Path $mismatchWork | Out-Null
Copy-Item (Join-Path $originals 'slides.pptx') (Join-Path $mismatchWork 'fake.docx') -Force
$since = (Get-LogText).Length
Rename-Item (Join-Path $mismatchWork 'fake.docx') 'fake.pdf'
$mismatchLine = Wait-Result -LeafName 'fake.pdf' -Since $since
$mismatchOk = ($mismatchLine -and $mismatchLine -match 'SKIPPED' -and $mismatchLine -match 'magic 不符')
Assert-True 'pptx 内容装成 .docx 被 magic 拦下' $mismatchOk $(if ($mismatchLine) { $mismatchLine.Trim() } else { '无结果行' })

# 只读文件
$roWork = Join-Path $Fixtures 'case-readonly'
New-Item -ItemType Directory -Force -Path $roWork | Out-Null
Copy-Item (Join-Path $originals 'report.docx') (Join-Path $roWork 'ro.docx') -Force
Set-ItemProperty -LiteralPath (Join-Path $roWork 'ro.docx') -Name IsReadOnly -Value $true
$since = (Get-LogText).Length
Rename-Item (Join-Path $roWork 'ro.docx') 'ro.pdf'
$roLine = Wait-Result -LeafName 'ro.pdf' -Since $since
$roOk = ($roLine -and $roLine -match 'OK')
Assert-True '只读文件转换成功' $roOk $(if ($roLine) { $roLine.Trim() } else { '无结果行' })
$roTarget = Join-Path $roWork 'ro.pdf'
Assert-True '只读位已恢复' ((Test-Path $roTarget) -and (Get-Item $roTarget).IsReadOnly)
$leftover = @(Get-ChildItem $roWork -Force -Directory | Where-Object { $_.Name -like '.renamepro-doc-*' })
Assert-True '无残留工作目录' ($leftover.Count -eq 0) ($leftover.Name -join ', ')

# 中文文件名（用字符码拼接，避开 PowerShell 5.1 的中文参数编码问题）
$cnWork = Join-Path $Fixtures 'case-chinesename'
New-Item -ItemType Directory -Force -Path $cnWork | Out-Null
$cnBase = [string][char]0x62A5 + [string][char]0x544A + ' v1'
Copy-Item (Join-Path $originals 'report.docx') (Join-Path $cnWork "$cnBase.docx") -Force
$since = (Get-LogText).Length
Rename-Item (Join-Path $cnWork "$cnBase.docx") "$cnBase.pdf"
$cnLine = Wait-Result -LeafName "$cnBase.pdf" -Since $since
$cnOk = ($cnLine -and $cnLine -match 'OK')
Assert-True '中文文件名转换成功' $cnOk $(if ($cnLine) { $cnLine.Trim() } else { '无结果行' })

# ---------------------------------------------------------------- 5) 图片回归

Write-Step '5) 图片转换回归（文档功能开启时不受影响）'
$imgWork = Join-Path $Fixtures 'case-image'
New-Item -ItemType Directory -Force -Path $imgWork | Out-Null
Copy-Item (Join-Path $originals 'photo.jpg') (Join-Path $imgWork 'photo.jpg') -Force
$since = (Get-LogText).Length
Rename-Item (Join-Path $imgWork 'photo.jpg') 'photo.png'
$imgLine = Wait-Result -LeafName 'photo.png' -Since $since
$imgOk = ($imgLine -and $imgLine -match 'OK')
Assert-True 'jpg → png 转换成功' $imgOk $(if ($imgLine) { $imgLine.Trim() } else { '无结果行' })
Assert-True '产物是真正的 PNG' (Test-Magic -Path (Join-Path $imgWork 'photo.png') -Expect 'png')

# ---------------------------------------------------------------- 6) 汇总

Write-Step '6) 汇总'
$log = Get-LogText
$okCount = @($log -split "`r?`n" | Where-Object { $_ -match '\[结果\] OK' }).Count
$failCount = @($log -split "`r?`n" | Where-Object { $_ -match '\[结果\] FAILED' }).Count
$skipCount = @($log -split "`r?`n" | Where-Object { $_ -match '\[结果\] SKIPPED' }).Count
Write-Host ("  log.txt  ：{0}" -f $logPath)
Write-Host ("  夹具目录 ：{0}（产物可直接打开人工检查）" -f $Fixtures)
Write-Host ("  日志统计 ：OK {0} 条，SKIPPED {1} 条，FAILED {2} 条" -f $okCount, $skipCount, $failCount)
Assert-True '日志里没有意外 FAILED' ($failCount -eq 0) "$failCount 条"

if (-not $KeepRunning) {
    Get-Process RenamePro -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
Remove-Item Env:\RENAMEPRO_SOFFICE -ErrorAction SilentlyContinue
Remove-Item Env:\RENAMEPRO_DOCENGINE -ErrorAction SilentlyContinue
Remove-Item Env:\RENAMEPRO_DOCROOT -ErrorAction SilentlyContinue

Write-Host ''
if ($failed -eq 0) {
    Write-Host ("文档转换校验：全部通过（{0} 项）" -f $passed) -ForegroundColor Green
    if ($KeepRunning) { Write-Host '托盘程序仍在运行（-KeepRunning），可在资源管理器里手动接着测。' -ForegroundColor Yellow }
    exit 0
}
Write-Host ("文档转换校验：{0} 项失败 / {1} 项通过" -f $failed, $passed) -ForegroundColor Red
exit 1
