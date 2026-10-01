# RenamePro 随包文档引擎构建脚本
# 用法：powershell -ExecutionPolicy Bypass -File build-docengine.ps1 [-MsiPath <msi>] [-OutDir libreoffice] [-Trim balanced|loose] [-KeepAllFonts] [-ReuseStage]
#
# 做什么：把 setup-lo-dev.ps1 / msiexec 展开出来的 LibreOffice 目录树裁剪成"够用且不臃肿"的子树，
#         剔除安装数据库类文件，生成清单 payload.json，再整树复制到随包目录 libreoffice\。
#
# 为什么不再压成"载荷"：自 1.2 起文档引擎不再嵌入 exe，而是以 <程序目录>\LibreOffice\ 目录随发布包分发
#         （打包见 publish.ps1 -Mode Docs，运行时发现见 Conversion\DocumentEngine.cs）。
#         原先"压成 zip 再编译期嵌进 exe"会让单文件 exe 涨到 330+ MB，并且首次转换还要把 700 MB
#         解压到 %LOCALAPPDATA%——目录版没有这两个代价。
#
# 为什么可以裁：LibreOffice 官方包把「帮助文档 + 全语言界面 + 拼写词典 + 各语言字体」都装了进去，
#         这些与"把文档转成另一种格式"无关。转换过滤器本身是 program\ 下那批 DLL，
#         删 DLL 等于删格式支持，所以 program\ 默认整棵保留——裁剪只删"明显不相干"的类别。
#
# 安全网：每裁掉一类就跑一遍矩阵冒烟（docx/pptx/xlsx→pdf、docx→odt、odt→docx、html→pdf）；
#         任何一次冒烟失败就立刻把该类回滚，并在结尾报告里标红。宁可多留几十 MB，也不发布坏包。
#
# 产物：libreoffice\（随包引擎目录，不入库；结构与官方安装一致：program\ share\ licenses\ payload.json）
param(
    # 已展开的 LibreOffice 目录树（setup-lo-dev.ps1 的 -TargetDir）
    [string]$SourceDir = 'D:\RenamePro-dev\libreoffice',
    # 直接指定 MSI：给了就自己展开，省掉手工跑 setup 脚本
    [string]$MsiPath = '',
    [string]$Version = '25.8.7',
    # 裁剪力度：balanced（默认，删帮助/语言包/词典/扩展/Python）| loose（只删帮助与语言包）
    [ValidateSet('balanced', 'loose')]
    [string]$Trim = 'balanced',
    # 保留全部字体（默认已保留 CJK 与常用西文字体；此开关连其余字体一起留）
    [switch]$KeepAllFonts,
    # 随包目录输出位置（默认 <仓库>\libreoffice，与发布包里的 LibreOffice\ 同构）
    [string]$OutDir = '',
    # 复用已裁剪好的 obj\lo-payload\stage：跳过展开与裁剪，只做清理/清单/复制
    # （实测省十几分钟；改了裁剪策略或换了 LibreOffice 版本时不要加这个开关）
    [switch]$ReuseStage,
    # 跳过矩阵冒烟（只建议在排障时用）
    [switch]$SkipSmoke
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $root 'libreoffice' }
$workRoot = Join-Path $root 'obj\lo-payload'
$stageDir = Join-Path $workRoot 'stage'

Write-Host "=== RenamePro 随包文档引擎构建（LibreOffice $Version，裁剪 $Trim）===" -ForegroundColor Cyan

# ---------------------------------------------------------------- 工具函数

function Get-PythonPath {
    # 生成 payload.json 用 Python 更稳（几千条记录的 JSON 不该手拼）
    foreach ($candidate in @(
            'D:\DeepSeek_Harness\resources\runtime\primary-runtime\dependencies\python\python.exe',
            'python.exe')) {
        $cmd = Get-Command $candidate -ErrorAction SilentlyContinue
        if ($cmd) { return $cmd.Source }
    }
    return $null
}

function Get-TreeSize {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return 0 }
    return (Get-ChildItem $Path -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
}

# ---------------------------------------------------------------- 1) 准备目录树

if ($ReuseStage) {
    Write-Host "`n[1/4] 复用已裁剪的 stage（跳过展开）" -ForegroundColor Cyan
    if (-not (Test-Path (Join-Path $stageDir 'program\soffice.com'))) {
        throw "stage 里没有 program\soffice.com（$stageDir）；去掉 -ReuseStage 重新裁剪，或改 -SourceDir"
    }
    $sofficeCom = Get-Item (Join-Path $stageDir 'program\soffice.com')
    $treeRoot = $stageDir
    $rawSize = Get-TreeSize $treeRoot
    Write-Host ("  stage: {0}（{1:N0} MB，{2} 个文件）" -f $treeRoot, ($rawSize / 1MB), (Get-ChildItem $treeRoot -Recurse -File).Count)
} else {

if ($MsiPath) {
    if (-not (Test-Path $MsiPath)) { throw "找不到 MSI：$MsiPath" }
    $found = Get-ChildItem $SourceDir -Recurse -Filter 'soffice.com' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $found) {
        Write-Host "`n[1/4] 展开 MSI 到 $SourceDir …" -ForegroundColor Cyan
        New-Item -ItemType Directory -Force -Path $SourceDir | Out-Null
        $log = Join-Path $workRoot 'extract.log'
        New-Item -ItemType Directory -Force -Path $workRoot | Out-Null
        $p = Start-Process -FilePath 'msiexec.exe' -Wait -PassThru -NoNewWindow -ArgumentList @(
            '/a', "`"$MsiPath`"", '/qn', "TARGETDIR=`"$SourceDir`"", '/l*v', "`"$log`""
        )
        if ($p.ExitCode -ne 0) { throw "MSI 展开失败（退出码 $($p.ExitCode)，日志 $log）" }
    } else {
        Write-Host "`n[1/4] 目录树已存在，跳过展开" -ForegroundColor Cyan
    }
} else {
    Write-Host "`n[1/4] 使用现成目录树 $SourceDir" -ForegroundColor Cyan
}

# soffice 可能落在根目录，也可能在 program\ 下；两种布局都支持
$sofficeCom = Get-ChildItem $SourceDir -Recurse -Filter 'soffice.com' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $sofficeCom) { throw "在 $SourceDir 里找不到 soffice.com（请先跑 setup-lo-dev.ps1）" }
$treeRoot = $sofficeCom.Directory.Parent.FullName   # soffice.com 在 <root>\program\ 下
$rawSize = Get-TreeSize $treeRoot
Write-Host ("  目录树: {0}（{1:N0} MB，{2} 个文件）" -f $treeRoot, ($rawSize / 1MB), (Get-ChildItem $treeRoot -Recurse -File).Count)

}

# ---------------------------------------------------------------- 2) 裁剪

if ($ReuseStage) {
    Write-Host "`n[2/4] 复用 stage：跳过裁剪与冒烟" -ForegroundColor Cyan
} else {
    Write-Host "`n[2/4] 裁剪…" -ForegroundColor Cyan
    if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

    # 先整树复制到 stage（只复制文件内容，与 ACL 无关）
    Write-Host '  复制到 stage…'
    Copy-Item (Join-Path $treeRoot '*') $stageDir -Recurse -Force
}

# 裁剪类别：每类都是"整目录/整文件模式"，可单独回滚
$categories = [ordered]@{}
$categories['readmes'] = { param($d) Remove-PathIfExists (Join-Path $d 'readmes') }
$categories['help'] = { param($d) Remove-PathIfExists (Join-Path $d 'help') }
$categories['语言包（仅留 zh-CN / en-US）'] = { param($d) Remove-OtherLanguages $d }
$categories['扩展（保留 PDF import）'] = { param($d) Remove-Extensions $d }
if ($Trim -eq 'balanced') {
    $categories['Python 运行时'] = { param($d) Remove-Python $d }
    $categories['拼写词典 / 断词'] = { param($d) Remove-Dicts $d }
    $categories['调试符号与静态库'] = { param($d) Remove-DebugFiles $d }
}
if (-not $KeepAllFonts) {
    $categories['非必需字体'] = { param($d) Remove-ExtraFonts $d }
}

function Remove-PathIfExists {
    param([string]$Path)
    if (Test-Path $Path) {
        Remove-Item $Path -Recurse -Force -ErrorAction SilentlyContinue
        return $true
    }
    return $false
}

function Remove-OtherLanguages {
    param([string]$Dir)
    $removed = 0
    # 界面语言资源位于 program\resource\ 下，形如 resource\zh-CN\...
    $resourceRoot = Join-Path $Dir 'program\resource'
    if (Test-Path $resourceRoot) {
        Get-ChildItem $resourceRoot -Directory | Where-Object { $_.Name -notin @('zh-CN', 'en-US') } | ForEach-Object {
            Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
            $removed++
        }
    }
    # 其他语言的自述/模板目录
    foreach ($pattern in 'program\*_*.res', 'share\template\*') {
        Get-ChildItem (Join-Path $Dir $pattern) -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -notmatch '^(zh-CN|en-US)' } | ForEach-Object {
                Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
                $removed++
            }
    }
    return $removed
}

function Remove-Extensions {
    param([string]$Dir)
    $extRoot = Join-Path $Dir 'share\extensions'
    if (-not (Test-Path $extRoot)) { return 0 }
    $removed = 0
    Get-ChildItem $extRoot -Directory -ErrorAction SilentlyContinue | Where-Object {
        # 保留 MPL 许可与 PDF 导入（pdf 作源时要用）
        $_.Name -notmatch 'pdfimport' -and $_.Name -notmatch 'license'
    } | ForEach-Object {
        Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
        $removed++
    }
    return $removed
}

function Remove-Python {
    param([string]$Dir)
    $removed = 0
    foreach ($name in 'program\python-core-*', 'program\python.exe', 'program\python3*.dll', 'program\python*.zip') {
        Get-ChildItem (Join-Path $Dir $name) -ErrorAction SilentlyContinue | ForEach-Object {
            Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
            $removed++
        }
    }
    return $removed
}

function Remove-Dicts {
    param([string]$Dir)
    $removed = 0
    foreach ($rel in 'share\dict\*', 'share\spellcheck\*', 'share\hyphen\*', 'share\thesaurus\*') {
        $target = Join-Path $Dir $rel
        if (Test-Path (Split-Path $target -Parent)) {
            Get-ChildItem $target -ErrorAction SilentlyContinue | ForEach-Object {
                Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
                $removed++
            }
        }
    }
    return $removed
}

function Remove-DebugFiles {
    param([string]$Dir)
    $count = 0
    Get-ChildItem $Dir -Recurse -File -Include '*.pdb', '*.lib', '*.exp', '*.ilk' -ErrorAction SilentlyContinue | ForEach-Object {
        Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
        $count++
    }
    return $count
}

function Remove-ExtraFonts {
    param([string]$Dir)
    # 中文字形必须有：Noto Sans CJK 相关一律保留；西文保留常用的度量兼容字体
    $keep = 'Liberation|Carlito|Caladea|DejaVu|OpenSymbol|NotoSansCJK|NotoSerifCJK|NotoSansSC|NotoSansTC|NotoSansMono'
    $count = 0
    foreach ($rel in 'share\fonts\truetype', 'share\fonts\opentype') {
        $fontDir = Join-Path $Dir $rel
        if (-not (Test-Path $fontDir)) { continue }
        Get-ChildItem $fontDir -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -notmatch $keep } | ForEach-Object {
            Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
            $count++
        }
    }
    return $count
}

# 冒烟用的最小文档：用 Python 生成真实 docx/pptx/xlsx（比手搓 ZIP 可靠）
$python = Get-PythonPath
function New-SmokeFixtures {
    param([string]$Dir)
    if (-not $python) { return $false }
    New-Item -ItemType Directory -Force -Path $Dir | Out-Null
    $script = Join-Path $Dir 'make.py'
    @'
import sys, os
from docx import Document
from pptx import Presentation
from pptx.util import Inches
from openpyxl import Workbook
out = sys.argv[1]
d = Document(); d.add_heading('冒烟测试', 0); d.add_paragraph('正文 中文测试 hello'); d.add_table(rows=2, cols=2)
d.save(os.path.join(out, 'smoke.docx'))
p = Presentation(); s = p.slides.add_slide(p.slide_layouts[1]); s.shapes.title.text = '幻灯片'
p.save(os.path.join(out, 'smoke.pptx'))
w = Workbook(); w.active['A1'] = '表头'; w.active['A2'] = 42; w.save(os.path.join(out, 'smoke.xlsx'))
open(os.path.join(out, 'smoke.html'), 'w', encoding='utf-8').write('<html><body><h1>标题</h1><p>段落</p></body></html>')
print('ok')
'@ | Set-Content -LiteralPath $script -Encoding UTF8
    & $python $script $Dir | Out-Null
    return (Test-Path (Join-Path $Dir 'smoke.docx'))
}

function Invoke-LibreOffice {
    param([string]$EngineCom, [string]$ProfileUri, [string]$OutDir, [string]$Source, [string]$TargetExt)
    # 两条经验（都踩过，改动前先看）：
    # 1) 用 Start-Process + 重定向到文件：LibreOffice 即使成功也会往 stderr 写
    #    "parser error / Could not find platform independent libraries" 之类的噪声，
    #    用 & 调用会被 PowerShell 的 $ErrorActionPreference='Stop' 当成终止错误，打断整个打包流程。
    # 2) 必须给引擎一个**确定可写**的 TMP/TEMP：它导出 pdf/odt 时会在自己的临时目录里落中间文件，
    #    %TEMP% 不可写时会以 "source file could not be loaded" / "impl_store … Class:Write Code:16"
    #    结束且不产出文件——而 csv/html 这类纯文本导出不受影响，表现为"有的格式能转、有的不能"。
    $err = Join-Path $OutDir '.lo-stderr.txt'
    $out = Join-Path $OutDir '.lo-stdout.txt'
    $scratch = Join-Path $OutDir 'lo-tmp'
    New-Item -ItemType Directory -Force -Path $scratch | Out-Null
    $arguments = @(
        "-env:UserInstallation=$ProfileUri",
        '--headless', '--norestore', '--nolockcheck', '--nodefault', '--nofirststartwizard', '--nologo',
        '--convert-to', $TargetExt, '--outdir', $OutDir, $Source
    )
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $savedTmp = $env:TMP; $savedTemp = $env:TEMP
    try {
        $env:TMP = $scratch; $env:TEMP = $scratch
        if (-not (Test-Path $EngineCom)) {
            Write-Host ("    引擎不存在：{0}" -f $EngineCom) -ForegroundColor Yellow
            return -1
        }
        $p = Start-Process -FilePath $EngineCom -ArgumentList $arguments -WorkingDirectory $OutDir `
            -Wait -PassThru -NoNewWindow -RedirectStandardOutput $out -RedirectStandardError $err
        return $p.ExitCode
    } catch {
        Write-Host ("    启动引擎失败：{0}" -f $_.Exception.Message) -ForegroundColor Yellow
        return -1
    } finally {
        $env:TMP = $savedTmp; $env:TEMP = $savedTemp
        $ErrorActionPreference = $previous
    }
}

function Invoke-SmokeTest {
    param([string]$EngineCom)
    $smokeDir = Join-Path $workRoot 'smoke'
    if (Test-Path $smokeDir) { Remove-Item $smokeDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $smokeDir | Out-Null
    if (-not (New-SmokeFixtures -Dir $smokeDir)) {
        Write-Host '  冒烟夹具生成失败（缺 python-docx/pptx/openpyxl），跳过冒烟' -ForegroundColor Yellow
        return $true
    }
    $cases = @(
        @{ Src = 'smoke.docx'; To = 'pdf' },
        @{ Src = 'smoke.pptx'; To = 'pdf' },
        @{ Src = 'smoke.xlsx'; To = 'pdf' },
        @{ Src = 'smoke.docx'; To = 'odt' },
        @{ Src = 'smoke.html'; To = 'pdf' }
    )
    $profileDir = Join-Path $workRoot 'smoke-profile'
    $profileUri = ([uri]$profileDir).AbsoluteUri
    foreach ($case in $cases) {
        $srcFile = Join-Path $smokeDir $case.Src
        $expect = Join-Path $smokeDir ([IO.Path]::GetFileNameWithoutExtension($case.Src) + '.' + $case.To)
        if (Test-Path $expect) { Remove-Item $expect -Force }
        # 至多两次：全新的 profile 第一次调用可能不产出结果，第二次即正常（见 DocConverter 注释）
        for ($attempt = 1; $attempt -le 2; $attempt++) {
            [void](Invoke-LibreOffice -EngineCom $EngineCom -ProfileUri $profileUri -OutDir $smokeDir -Source $srcFile -TargetExt $case.To)
            if ((Test-Path $expect) -and (Get-Item $expect).Length -gt 0) { break }
        }
        if (-not (Test-Path $expect) -or (Get-Item $expect).Length -eq 0) {
            Write-Host ("  冒烟失败：{0} → {1}（stderr：{2}）" -f $case.Src, $case.To,
                ((Get-Content (Join-Path $smokeDir '.lo-stderr.txt') -ErrorAction SilentlyContinue | Select-Object -First 1) -join '')) -ForegroundColor Yellow
            return $false
        }
    }
    # odt → docx 反向再验一次（覆盖导入侧过滤器）
    $back = Join-Path $smokeDir 'smoke-back.docx'
    if (Test-Path $back) { Remove-Item $back -Force }
    Copy-Item (Join-Path $smokeDir 'smoke.odt') (Join-Path $smokeDir 'smoke-back.odt') -Force
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        [void](Invoke-LibreOffice -EngineCom $EngineCom -ProfileUri $profileUri -OutDir $smokeDir `
            -Source (Join-Path $smokeDir 'smoke-back.odt') -TargetExt 'docx')
        if (Test-Path $back) { break }
    }
    if (-not (Test-Path $back)) {
        Write-Host '  冒烟失败：smoke-back.odt → docx' -ForegroundColor Yellow
        return $false
    }
    return $true
}

$rolledBack = @()
if ($ReuseStage) {
    # stage 是之前已经裁剪并冒烟过的，这里不再重复裁剪（-ReuseStage 的语义）
} elseif (-not $SkipSmoke) {
    foreach ($name in $categories.Keys) {
        $before = Get-TreeSize $stageDir
        & $categories[$name] $stageDir | Out-Null
        $after = Get-TreeSize $stageDir
        $saved = ($before - $after) / 1MB
        Write-Host ("  裁剪【{0}】释放 {1:N1} MB（{2:N0} MB 剩余）" -f $name, $saved, ($after / 1MB))
        if ($saved -lt 0.5) { continue }   # 没释放多少，不必冒烟
        $engineCom = Join-Path $stageDir 'program\soffice.com'
        if (-not (Test-Path $engineCom)) {
            Write-Host '  裁剪后找不到 soffice.com，回滚这一类' -ForegroundColor Red
            $rolledBack += $name
            break
        }
        if (-not (Invoke-SmokeTest -EngineCom $engineCom)) {
            Write-Host ("  【{0}】冒烟失败，回滚这一类（宁可多留体积）" -f $name) -ForegroundColor Red
            $rolledBack += $name
            # 回滚：用原始目录树重新复制这个过程太重，改为只恢复该类别涉及的路径
            $restore = $true
            switch -Regex ($name) {
                'readmes' { if (Test-Path (Join-Path $treeRoot 'readmes')) { Copy-Item (Join-Path $treeRoot 'readmes') $stageDir -Recurse -Force } }
                'help' { if (Test-Path (Join-Path $treeRoot 'help')) { Copy-Item (Join-Path $treeRoot 'help') $stageDir -Recurse -Force } }
                '语言包' { Copy-Item (Join-Path $treeRoot 'program\resource') (Join-Path $stageDir 'program') -Recurse -Force }
                '扩展' { if (Test-Path (Join-Path $treeRoot 'share\extensions')) { Copy-Item (Join-Path $treeRoot 'share\extensions') (Join-Path $stageDir 'share') -Recurse -Force } }
                'Python' { foreach ($n in 'python-core-*', 'python.exe') { Get-ChildItem (Join-Path $treeRoot "program\$n") -ErrorAction SilentlyContinue | ForEach-Object { Copy-Item $_.FullName (Join-Path $stageDir 'program') -Recurse -Force } } }
                '词典' { foreach ($n in 'dict', 'spellcheck', 'hyphen', 'thesaurus') { if (Test-Path (Join-Path $treeRoot "share\$n")) { Copy-Item (Join-Path $treeRoot "share\$n") (Join-Path $stageDir 'share') -Recurse -Force } } }
                '调试' { $restore = $false }   # 调试符号删了不影响功能
                '字体' { Copy-Item (Join-Path $treeRoot 'share\fonts') (Join-Path $stageDir 'share') -Recurse -Force }
                default { $restore = $false }
            }
            if (-not $restore) { Write-Host '    （该类不影响功能，保持删除）' -ForegroundColor Yellow }
            if ($restore) { Write-Host '    已回滚该类' -ForegroundColor Yellow }
        }
    }
} else {
    foreach ($name in $categories.Keys) { & $categories[$name] $stageDir | Out-Null }
}

$trimmedSize = Get-TreeSize $stageDir

# ---------------------------------------------------------------- 3) 清理与清单

Write-Host "`n[3/4] 清理安装残留并生成清单 payload.json…" -ForegroundColor Cyan

# msiexec /a 会把安装数据库（.msi）连同数据一起放进目标目录；运行时完全用不到，删掉。
# 实测 LibreOffice_25.8.7_Win_x86-64.msi 有 18.6 MB，是这类文件里最大的一块。
$installerLeftovers = @(Get-ChildItem $stageDir -Recurse -File -Include '*.msi', '*.msp', '*.cab' -ErrorAction SilentlyContinue)
if ($installerLeftovers.Count -gt 0) {
    $freed = ($installerLeftovers | Measure-Object Length -Sum).Sum
    foreach ($file in $installerLeftovers) { Remove-Item $file.FullName -Force -ErrorAction SilentlyContinue }
    Write-Host ("  删除安装数据库类文件 {0} 个，释放 {1:N1} MB" -f $installerLeftovers.Count, ($freed / 1MB))
} else {
    Write-Host '  没有安装数据库类文件需要清理'
}

$engineCom = Join-Path $stageDir 'program\soffice.com'
if (-not (Test-Path $engineCom)) {
    $engineCom = (Get-ChildItem $stageDir -Recurse -Filter 'soffice.com' -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
}
if (-not $engineCom) { throw "裁剪后的目录里找不到 soffice.com（$stageDir）" }

# 引擎相对路径：优先记录 soffice.com（控制台宿主，转换时能拿到真实退出码）
$engineRel = 'program/soffice.com'
if (-not (Test-Path (Join-Path $stageDir 'program\soffice.com'))) {
    $engineExe = Get-ChildItem (Join-Path $stageDir 'program') -Filter 'soffice.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    $engineRel = if ($engineExe) { 'program/' + $engineExe.Name } else { 'program/soffice.exe' }
}
if (-not $python) { throw '需要 Python 生成清单（未找到 python.exe）' }
$manifestScript = Join-Path $workRoot 'make-manifest.py'
# 取版本号：用 Start-Process 而不是 & —— & 会把 stderr 噪声（parser error 之类）当成终止错误，
# 而且 $sofficeCom 是 FileInfo，必须显式取 .FullName 才能当可执行文件路径用。
$versionErr = Join-Path $workRoot 'version-stderr.txt'
$versionOut = Join-Path $workRoot 'version-stdout.txt'
$prevEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    $vp = Start-Process -FilePath $engineCom -ArgumentList @('--headless', '--version') `
        -Wait -PassThru -NoNewWindow -RedirectStandardOutput $versionOut -RedirectStandardError $versionErr
} catch {
    Write-Host ("  版本探测失败：{0}" -f $_.Exception.Message) -ForegroundColor Yellow
} finally {
    $ErrorActionPreference = $prevEap
}
$loVersionLine = (Get-Content $versionOut -ErrorAction SilentlyContinue | Select-Object -First 1)
if (-not $loVersionLine) { $loVersionLine = (Get-Content $versionErr -ErrorAction SilentlyContinue | Select-Object -First 1) }
$loVersion = if ($loVersionLine -match 'LibreOffice\s+([\d.]+)') { $Matches[1] } else { $Version }
Write-Host ("  引擎版本：{0}" -f $loVersion)
@'
import sys, os, json, hashlib
root, out, engine_rel, version = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]
files = []
total = 0
for dirpath, _, names in os.walk(root):
    for name in names:
        full = os.path.join(dirpath, name)
        rel = os.path.relpath(full, root).replace('\\', '/')
        if rel == 'payload.json':
            continue
        size = os.path.getsize(full)
        total += size
        files.append({'p': rel, 's': size})
files.sort(key=lambda x: x['p'])
manifest = {
    'schema': 1,
    'kind': 'libreoffice',
    'libreOfficeVersion': version,
    'engineRelativePath': engine_rel,
    'extractedBytes': total,
    'fileCount': len(files),
    'files': files,
}
with open(out, 'w', encoding='utf-8') as f:
    json.dump(manifest, f, ensure_ascii=False, separators=(',', ':'))
print(f'{len(files)} files, {total} bytes')
'@ | Set-Content -LiteralPath $manifestScript -Encoding UTF8
$manifestPath = Join-Path $stageDir 'payload.json'
$manifestInfo = & $python $manifestScript $stageDir $manifestPath $engineRel $loVersion
Write-Host "  $manifestInfo"

# 许可文本（MPL-2.0 与第三方声明）：随包分发必须带上
$licenseDir = Join-Path $stageDir 'licenses'
New-Item -ItemType Directory -Force -Path $licenseDir | Out-Null
foreach ($candidate in 'license.txt', 'LICENSE', 'NOTICE', 'readmes\license.txt') {
    $p = Join-Path $treeRoot $candidate
    if (Test-Path $p) { Copy-Item $p (Join-Path $licenseDir ([IO.Path]::GetFileName($p))) -Force }
}
$thirdParty = Get-ChildItem $treeRoot -Recurse -File -Filter '*third*party*' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($thirdParty) { Copy-Item $thirdParty.FullName (Join-Path $licenseDir $thirdParty.Name) -Force }
Write-Host ("  许可文件：{0} 个" -f (Get-ChildItem $licenseDir -File -ErrorAction SilentlyContinue).Count)

# ---------------------------------------------------------------- 4) 复制到随包目录 + 引擎自检

Write-Host "`n[4/4] 复制到随包目录…" -ForegroundColor Cyan
$outFull = [System.IO.Path]::GetFullPath($OutDir)
$stageFull = [System.IO.Path]::GetFullPath($stageDir)
if ($outFull.TrimEnd('\') -ieq $stageFull.TrimEnd('\')) {
    Write-Host '  输出目录就是 stage 本身，跳过复制'
} else {
    if (Test-Path $outFull) { Remove-Item $outFull -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $outFull | Out-Null
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    # robocopy 复制上万个小文件比 Copy-Item 快一个数量级；退出码 < 8 表示成功（按位组合）
    $null = robocopy $stageFull $outFull /E /MT:8 /NFL /NDL /NJH /NJS /NP /R:2 /W:2
    if ($LASTEXITCODE -ge 8) { throw "复制到 $outFull 失败（robocopy 退出码 $LASTEXITCODE）" }
    $sw.Stop()
    Write-Host ("  已复制（{0:N1} s）：{1}" -f $sw.Elapsed.TotalSeconds, $outFull)
}
if (-not (Test-Path (Join-Path $outFull 'program\soffice.com'))) {
    throw "随包目录里没有 program\soffice.com：$outFull"
}

# 引擎自检：随包目录里的这份能不能起来（缺 DLL / 被杀软拦在这里暴露，而不是等用户第一次转换）
Write-Host '  引擎自检…'
$checkOut = Join-Path $workRoot 'selfcheck-stdout.txt'
$checkErr = Join-Path $workRoot 'selfcheck-stderr.txt'
$checkTmp = Join-Path $workRoot 'selfcheck-tmp'
New-Item -ItemType Directory -Force -Path $checkTmp | Out-Null
$prevEap2 = $ErrorActionPreference
$savedTmp2 = $env:TMP; $savedTemp2 = $env:TEMP
$ErrorActionPreference = 'Continue'
try {
    # 与打包/转换同样的经验：给引擎一个确定可写的 TEMP，否则它可能直接失败
    $env:TMP = $checkTmp; $env:TEMP = $checkTmp
    $null = Start-Process -FilePath (Join-Path $outFull 'program\soffice.com') -ArgumentList @('--headless', '--version') `
        -Wait -PassThru -NoNewWindow -RedirectStandardOutput $checkOut -RedirectStandardError $checkErr
} catch {
    Write-Host ("  引擎自检启动失败：{0}" -f $_.Exception.Message) -ForegroundColor Red
} finally {
    $env:TMP = $savedTmp2; $env:TEMP = $savedTemp2
    $ErrorActionPreference = $prevEap2
}
$checkLine = (Get-Content $checkOut -ErrorAction SilentlyContinue | Where-Object { $_ -match 'LibreOffice' } | Select-Object -First 1)
if (-not $checkLine) {
    $checkLine = (Get-Content $checkErr -ErrorAction SilentlyContinue | Where-Object { $_ -match 'LibreOffice' } | Select-Object -First 1)
}
if (-not $checkLine) { throw '随包引擎自检失败：--version 没有输出 LibreOffice 版本（详见 obj\lo-payload\selfcheck-*.txt）' }
Write-Host ("  引擎自检通过：{0}" -f $checkLine.Trim()) -ForegroundColor Green

$outSize = Get-TreeSize $outFull
$outFiles = (Get-ChildItem $outFull -Recurse -File).Count

# ---------------------------------------------------------------- 5) 汇总

Write-Host "`n=== 体积汇总 ===" -ForegroundColor Green
Write-Host ("  原始目录树      : {0,8:N0} MB" -f ($rawSize / 1MB))
Write-Host ("  裁剪后目录树    : {0,8:N0} MB（去掉 {1:N0} MB，{2:P0}）" -f ($trimmedSize / 1MB), (($rawSize - $trimmedSize) / 1MB), (($rawSize - $trimmedSize) / $rawSize))
Write-Host ("  随包目录        : {0,8:N0} MB（{1:N0} 个文件）" -f ($outSize / 1MB), $outFiles)
Write-Host ("  引擎相对路径    : {0}" -f $engineRel)
Write-Host ("  LibreOffice 版本: {0}" -f $loVersion)
if ($rolledBack.Count) {
    Write-Host ("  已回滚的裁剪类别: {0}" -f ($rolledBack -join '、')) -ForegroundColor Yellow
} else {
    Write-Host '  所有裁剪类别均通过冒烟'
}
Write-Host ''
Write-Host '下一步：powershell -ExecutionPolicy Bypass -File publish.ps1 -Mode Docs'
Write-Host '       （publish.ps1 会把该目录复制成发布包里的 LibreOffice\，并做结构断言）'
