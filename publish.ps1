# RenamePro 便携打包脚本：图片版 / 音视频版（单文件 exe）+ 全功能版（目录包，含 LibreOffice\）
# 用法：powershell -ExecutionPolicy Bypass -File publish.ps1 [-Mode Image|Av|Docs|All] [-Version 2.0.0] [-SkipEngineBuild]
#
# 三个版本的包内结构（v2.0 起）：
#   图片版   Image：RenamePro.exe + 使用说明.txt（不嵌入任何载荷 → 只做图片转换）
#   音视频版 Av   ：RenamePro.exe + 使用说明.txt（exe 内含 FFmpeg 载荷，首次音视频转换解压到
#                   %LOCALAPPDATA%\RenamePro\runtime\，约 150 ms，只此一次）
#                   （注意：图片转换用的是主程序内置的 Magick.NET，没有独立载荷可拆，
#                    所以"音视频版"本身就包含图片功能）
#   全功能版 Docs ：RenamePro.exe + LibreOffice\ + 使用说明.txt
#                   —— **目录包**：文档引擎（裁剪后的 LibreOffice 25.8.7）以 LibreOffice\ 目录随包分发，
#                      不再嵌进 exe。因此没有"首次解压几百 MB"这件事，exe 也只有 83 MB 而不是 330+ MB；
#                      代价是必须整个目录一起复制/移动（见使用说明与 README「文档转换引擎」）。
#
# FFmpeg 载荷在**编译期**嵌入，所以图片版与音视频版是两次独立发布；用 MSBuild 开关控制：
#   -p:EmbedFfmpegPayload=true|false
#
# 全功能版的引擎树来自 build-docengine.ps1（默认输出 libreoffice\）；缺失时会自动构建（需要
# setup-lo-dev.ps1 展开出的原始目录树或 MSI，见该脚本头注释）。已就绪时加 -SkipEngineBuild 跳过检查。
param(
    [ValidateSet('Image', 'Av', 'Docs', 'All', 'Full', 'Doc', 'Both')]
    [string]$Mode = 'All',
    [string]$Version = '2.0.0',
    # 引擎树已就绪时跳过构建检查（仓库里已有 libreoffice\program\soffice.com 时最省事）
    [switch]$SkipEngineBuild
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'RenamePro.csproj'
$distDir = Join-Path $root 'dist'
$stageDir = Join-Path $root 'obj\publish-stage'
$ffmpegDir = Join-Path $root 'ffmpeg'
$payloadPath = Join-Path $root 'Assets\ffmpeg-payload.zip'
$engineDir = Join-Path $root 'libreoffice'

# 兼容旧参数名：Full→Av、Doc→Docs、Both→All
switch ($Mode) {
    'Full' { $Mode = 'Av' }
    'Doc' { $Mode = 'Docs' }
    'Both' { $Mode = 'All' }
}

$wantImage = $Mode -in @('Image', 'All')
$wantAv = $Mode -in @('Av', 'All')
$wantDocs = $Mode -in @('Docs', 'All')

Write-Host "=== RenamePro 便携打包（v$Version，模式 $Mode）===" -ForegroundColor Cyan

# 1) 清理输出
Remove-Item $distDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $stageDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $distDir | Out-Null

# ---------------------------------------------------------------- 使用说明文本
$readmeCommon = @'
RenamePro 使用说明
==================

一、这是什么
  在资源管理器里给文件改后缀（例如把 a.jpg 改成 a.png），程序会在后台自动：
    1) 生成原格式副本（a - 副本.jpg，程序永不自动删除）
    2) 把文件内容真正转换为新格式并原子替换
    3) 显示系统进度框（可在 config.json 中关闭）

二、怎么用
  1. 双击 RenamePro.exe：无窗口，直接驻留系统托盘（单实例，重复双击不会开第二个）
  2. 在资源管理器里改文件名/后缀即可，例如：
       a.jpg  → a.png   图片转换
       b.mkv  → b.mp4   视频（优先流拷贝，秒级完成）
       c.flac → c.mp3   音频
  3. 托盘图标右键菜单：
        暂停监听 / 继续监听   临时停止自动转换
        开机自启动            勾选后登录自动运行（首次会弹一次 UAC，允许即可）
        重新加载配置          修改 config.json 后点它即时生效
        文档引擎：…           显示当前解析到的文档转换引擎（排障第一现场）
        退出                  完全关闭程序

三、配置（config.json）
  首次运行会在程序目录自动生成，带中文注释，可用记事本直接修改：
        enableBackup           是否创建原格式副本（建议 true）
        autoRollbackOnFailure  转换失败时是否自动回滚（删除目标文件并把副本移回原名）
        enableToast            全部任务完成后是否弹 Windows 通知
        showProgressDialog     是否显示系统进度框
        imageQuality           JPEG 输出质量 1~100
        gifPolicy              first-frame 取首帧 / skip 忽略 gif 改名
        imageConcurrency       图片队列并发度
        watchDrives            只监听指定盘符，留空 = 全部固定磁盘
        skipCloudFiles         是否跳过云盘按需占位文件
        documentConversion     是否启用文档转换（false = 改文档后缀只写日志跳过）
        documentEngine         auto / libreoffice / bundled / com，文档引擎选择
        allowPdfSource         是否允许把 PDF 当源（默认 false）
        docTimeoutSeconds      单个文档任务的超时秒数（30~1800）
        docMaxConcurrency      文档队列并发度（1~4，默认 1）

四、日志
  程序目录 log.txt：每次转换的时间、旧路径 → 新路径、结果（OK/FAILED/CANCELLED/SKIPPED）与错误详情。
'@

$readmeAv = $readmeCommon + @'

五、音视频转换（本包已内置 ffmpeg）
  音视频转换所需的 ffmpeg 已内置在 RenamePro.exe 里，本目录只有这一个程序文件。
  首次进行音视频转换时会把内置的 ffmpeg 解压到：
      %LOCALAPPDATA%\RenamePro\runtime\<载荷ID>\
  只解压一次（约 1~2 秒），之后每次启动直接复用，属于正常行为。
  如果该目录被安全软件拦截，可把它加入白名单；解压失败时音视频功能会被禁用并写入 log.txt，
  图片转换不受影响。

六、本包不含文档转换引擎
  本包未内置无头 LibreOffice：改文档后缀（docx / pptx / xlsx 等）时会写日志并跳过。
  需要文档转换时请用全功能版；如果本机已装 LibreOffice，也可以把 config.json 的
  documentEngine 设为 "libreoffice" 让它走系统安装的那份。
'@

$readmeImage = $readmeCommon + @'

五、音视频转换（本轻量版不含 ffmpeg）
  本包体积最小，仅包含图片转换：改音视频后缀时会写日志并跳过（不弹任何提示）。
  需要音视频转换时，请改用音视频版或全功能版。

六、文档转换（本包不含引擎）
  本包未内置 LibreOffice，也没有 ffmpeg：改文档后缀时会写日志并跳过。
  唯一例外是 markdown → Word（notes.md → notes.docx），它由内置转换器完成，本包同样可用。
'@

$readmeDocs = $readmeCommon + @'

五、文档转换（本包已内置文档引擎，整个目录一起用）
  本目录结构：
      RenamePro.exe      主程序
      LibreOffice\       文档转换引擎（裁剪后的 LibreOffice，无需安装、无需解压等待）
      使用说明.txt       本文件

  **不要只把 RenamePro.exe 复制走**：那样就丢了 LibreOffice\ 目录，文档转换会退化为
  "只能用本机已安装的 LibreOffice / Microsoft Office"。要移动就整个目录一起移动。

  引擎发现顺序（config.json 的 documentEngine = "auto" 时）：
      1) 本目录的 LibreOffice\
      2) 本机安装的 LibreOffice
      3) 本机安装的 Microsoft Office（Word / PowerPoint / Excel）
  想强制只用其中某一种：把 documentEngine 设为 "bundled" / "libreoffice" / "com"。

  首次文档转换会创建 LibreOffice 用户配置目录（%LOCALAPPDATA%\RenamePro\DocEngine），
  托盘启动时的后台预热通常已经把它建好了，所以第一次转换一般也是秒级。

  占用空间：本目录约 790 MB（RenamePro.exe 83 MB + LibreOffice\ 703 MB）。
  许可：LibreOffice 以 MPL-2.0 分发，许可与第三方声明在 LibreOffice\licenses\，
        详见项目 README 的「第三方组件与许可」。

六、文档转换能做什么
  同族互转：docx / doc / odt / rtf / md / html 之间、pptx / ppt / odp 之间、xlsx / xls / ods / csv 之间；
  转 pdf：上面任何格式都可以把后缀改成 .pdf；
  markdown 转 Word：notes.md → notes.docx（由内置转换器完成，不需要任何引擎）。
  跨族转换（例如 docx → xlsx）按设计拒绝：表格请改成 csv 或 pdf。
  内容与后缀不符的文件（例如把 PNG 改名成 .docx）会被跳过，不会转换。
'@

# ---------------------------------------------------------------- 工具函数

# 生成 FFmpeg 载荷（把 ffmpeg\ 目录压成单个 zip，供 EmbeddedResource 嵌入）
function New-FfmpegPayload {
    $sources = Get-ChildItem $ffmpegDir -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -ne '.bak' -and $_.Name -notlike '*.hold' }
    if (-not $sources) {
        Write-Host "错误：$ffmpegDir 里没有 FFmpeg 文件，无法生成音视频版" -ForegroundColor Red
        Write-Host '  请先运行：powershell -ExecutionPolicy Bypass -File build-ffmpeg.ps1' -ForegroundColor Yellow
        Write-Host '  或手工把 ffmpeg.exe / ffprobe.exe 及运行时 DLL 放进该目录' -ForegroundColor Yellow
        exit 1
    }
    foreach ($required in @('ffmpeg.exe', 'ffprobe.exe')) {
        if (-not ($sources.Name -contains $required)) {
            Write-Host "错误：$ffmpegDir 缺少 $required" -ForegroundColor Red
            exit 1
        }
    }

    $tempStaging = Join-Path $env:TEMP ("renamepro-payload-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $tempStaging | Out-Null
    try {
        foreach ($file in $sources) {
            Copy-Item $file.FullName (Join-Path $tempStaging $file.Name) -Force
        }
        Remove-Item $payloadPath -Force -ErrorAction SilentlyContinue
        Compress-Archive -Path (Join-Path $tempStaging '*') -DestinationPath $payloadPath -CompressionLevel Optimal
    } finally {
        Remove-Item $tempStaging -Recurse -Force -ErrorAction SilentlyContinue
    }

    $mb = [math]::Round((Get-Item $payloadPath).Length / 1MB, 2)
    Write-Host ("FFmpeg 载荷：{0} 个文件 → Assets\ffmpeg-payload.zip（{1} MB）" -f $sources.Count, $mb)

    # 与 RuntimePayload 的必需清单对账，防止改漏 DLL
    $expected = @(
        'ffmpeg.exe', 'ffprobe.exe',
        'libx264-165.dll', 'libmp3lame-0.dll', 'libopus-0.dll', 'libvorbis-0.dll',
        'libvorbisenc-2.dll', 'libogg-0.dll', 'libvpx-1.dll',
        'libiconv-2.dll', 'libwinpthread-1.dll', 'zlib1.dll'
    )
    $missing = $expected | Where-Object { -not ($sources.Name -contains $_) }
    $extra = $sources.Name | Where-Object { -not ($expected -contains $_) }
    if ($missing) {
        Write-Host ("警告：载荷缺少清单内文件：{0}" -f ($missing -join '、')) -ForegroundColor Yellow
    }
    if ($extra) {
        Write-Host ("警告：载荷包含清单外文件：{0}（RuntimePayload 的必需清单未同步）" -f ($extra -join '、')) -ForegroundColor Yellow
    }
}

# 找 7-Zip（压缩几百 MB 的目录包时比 Compress-Archive 快很多）
function Find-SevenZip {
    foreach ($candidate in @(
            'C:\Program Files\7-Zip\7z.exe',
            'C:\Program Files (x86)\7-Zip\7z.exe',
            (Join-Path $env:LOCALAPPDATA 'Programs\7-Zip\7z.exe'))) {
        if (Test-Path $candidate) { return $candidate }
    }
    $cmd = Get-Command '7z.exe' -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

# 把 <stage>\<包名> 压成 dist\<包名>-v<版本>.zip；包内保留顶层目录名（用户解压得到同名目录）
function Compress-PackageFolder {
    param([string]$FolderPath, [string]$ZipPath)
    $parent = Split-Path $FolderPath -Parent
    $name = Split-Path $FolderPath -Leaf
    $sevenZip = Find-SevenZip
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    if ($sevenZip) {
        Write-Host ("  使用 7-Zip 压缩（{0}）" -f $sevenZip)
        Push-Location $parent
        try {
            & $sevenZip 'a' '-tzip' '-mx=7' '-mmt=on' $ZipPath $name | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "7-Zip 压缩失败（退出码 $LASTEXITCODE）" }
        } finally {
            Pop-Location
        }
    } else {
        Write-Host '  使用 Compress-Archive 压缩（未找到 7-Zip，会慢一些）' -ForegroundColor Yellow
        Compress-Archive -Path $FolderPath -DestinationPath $ZipPath -CompressionLevel Optimal
    }
    $sw.Stop()
    Write-Host ("  压缩耗时 {0:N1} s" -f $sw.Elapsed.TotalSeconds)
}

# 发布一次单文件 exe，返回 exe 路径。
# FFmpeg 开关在这里显式传入：MSBuild 属性一旦显式给出，csproj 里的默认值就不再参与，
# 因此每个版本拿到的载荷组合是确定的，不依赖仓库里此刻有哪些载荷文件。
function Publish-SingleFileExe {
    param([bool]$EmbedFfmpeg)
    Remove-Item $stageDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
    $ffmpegSwitch = if ($EmbedFfmpeg) { 'true' } else { 'false' }
    Write-Host ("  发布：FFmpeg 载荷={0}，版本={1}" -f $ffmpegSwitch, $Version)
    # Out-Host：让 dotnet 的输出直接落屏，避免被当成函数返回值混进 $exe
    dotnet publish $project -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishReadyToRun=false `
        -p:DebugType=none `
        -p:Version=$Version `
        -p:FileVersion=$Version.0 `
        -p:AssemblyVersion=$Version.0 `
        -p:EmbedFfmpegPayload=$ffmpegSwitch `
        -o $stageDir | Out-Host
    if ($LASTEXITCODE -ne 0) { Write-Host "发布失败（退出码 $LASTEXITCODE）" -ForegroundColor Red; exit $LASTEXITCODE }

    $exe = Join-Path $stageDir 'RenamePro.exe'
    if (-not (Test-Path $exe)) { Write-Host '未生成 RenamePro.exe' -ForegroundColor Red; exit 1 }
    return $exe
}

# 单文件包（图片版 / 音视频版）：包内只有 exe + 使用说明
function New-PortablePackage {
    param(
        [string]$ExePath,
        [string]$PackageName,
        [string]$Readme
    )
    $folder = Join-Path $stageDir $PackageName
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    Copy-Item $ExePath (Join-Path $folder 'RenamePro.exe')
    [System.IO.File]::WriteAllText((Join-Path $folder '使用说明.txt'), $Readme, (New-Object System.Text.UTF8Encoding($true)))

    $files = Get-ChildItem $folder -File
    Write-Host ("  {0} 包内文件：{1}" -f $PackageName, (($files | Select-Object -ExpandProperty Name) -join '、'))
    if ($files.Count -ne 2) {
        Write-Host '  警告：包内应为 2 个文件（exe + 说明）' -ForegroundColor Yellow
    }

    $zipPath = Join-Path $distDir ($PackageName + "-v$Version.zip")
    Compress-PackageFolder -FolderPath $folder -ZipPath $zipPath
    $zipMb = (Get-Item $zipPath).Length / 1MB
    Write-Host ("已生成：{0}（{1:N1} MB）" -f $zipPath, $zipMb) -ForegroundColor Green
    $script:packageSummary += [pscustomobject]@{ 包 = $PackageName; 文件 = $files.Count; zip_MB = [math]::Round($zipMb, 1) }
}

# 全功能版：目录包（exe + LibreOffice\ + 使用说明），文档引擎随包分发
function New-DocsPackage {
    param(
        [string]$ExePath,
        [string]$EngineDir,
        [string]$PackageName,
        [string]$Readme
    )
    if (-not (Test-Path (Join-Path $EngineDir 'program\soffice.com'))) {
        throw "引擎目录里没有 program\soffice.com：$EngineDir（先跑 build-docengine.ps1）"
    }

    $folder = Join-Path $stageDir $PackageName
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    Copy-Item $ExePath (Join-Path $folder 'RenamePro.exe')
    [System.IO.File]::WriteAllText((Join-Path $folder '使用说明.txt'), $Readme, (New-Object System.Text.UTF8Encoding($true)))

    # 发布包里的目录名固定为 LibreOffice（DocumentEngine 就按这个名字找；程序内另有一份
    # DocEngine\ 用于 LibreOffice 用户配置，两者大小写不同名，不会撞车）
    $engineTarget = Join-Path $folder 'LibreOffice'
    Write-Host ("  复制引擎树 → {0}" -f $engineTarget)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    # 排除安装数据库类文件（msiexec /a 会把它一起放进目标目录，运行时用不到）
    $null = robocopy $EngineDir $engineTarget /E /MT:8 /XF *.msi *.msp *.cab /NFL /NDL /NJH /NJS /NP /R:2 /W:2
    if ($LASTEXITCODE -ge 8) { throw "复制引擎树失败（robocopy 退出码 $LASTEXITCODE）" }
    $sw.Stop()
    if (-not (Test-Path (Join-Path $engineTarget 'program\soffice.com'))) {
        throw "复制后缺少 LibreOffice\program\soffice.com：$engineTarget"
    }
    Write-Host ("  引擎树复制完成（{0:N1} s）" -f $sw.Elapsed.TotalSeconds)

    # 结构断言：exe 必须能作为单文件运行、清单必须在、引擎文件必须齐
    $engineFiles = (Get-ChildItem $engineTarget -Recurse -File).Count
    $engineMb = (Get-ChildItem $engineTarget -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
    $folderFiles = (Get-ChildItem $folder -Recurse -File).Count
    $folderMb = (Get-ChildItem $folder -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
    Write-Host ("  包内：{0} 个文件 / {1:N0} MB（其中 LibreOffice\ {2} 个文件 / {3:N0} MB）" -f
        $folderFiles, $folderMb, $engineFiles, $engineMb)
    if (-not (Test-Path (Join-Path $engineTarget 'payload.json'))) {
        Write-Host '  警告：引擎目录没有 payload.json（自检会跳过清单核对）' -ForegroundColor Yellow
    }

    $zipPath = Join-Path $distDir ($PackageName + "-v$Version.zip")
    Compress-PackageFolder -FolderPath $folder -ZipPath $zipPath

    # 压缩后再验一次：条目数太少说明 LibreOffice\ 没进包（例如误用了不跟随目录的压缩方式）
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try { $entries = $zip.Entries.Count } finally { $zip.Dispose() }
    Write-Host ("  zip 条目数：{0:N0}" -f $entries)
    if ($entries -lt 4000) {
        throw "zip 里只有 $entries 个条目，LibreOffice\ 很可能没被打进去：$zipPath"
    }

    $zipMb = (Get-Item $zipPath).Length / 1MB
    Write-Host ("已生成：{0}（{1:N1} MB）" -f $zipPath, $zipMb) -ForegroundColor Green
    $script:packageSummary += [pscustomobject]@{ 包 = $PackageName; 文件 = $folderFiles; zip_MB = [math]::Round($zipMb, 1) }
}

# ---------------------------------------------------------------- 主流程
$script:packageSummary = @()
try {
    if ($wantImage) {
        # 图片版：不嵌入 FFmpeg 载荷
        $exe = Publish-SingleFileExe -EmbedFfmpeg $false
        Write-Host ("单文件 exe 生成：{0:N1} MB（不含任何载荷）" -f ((Get-Item $exe).Length / 1MB))
        New-PortablePackage -ExePath $exe -PackageName 'RenamePro-image' -Readme $readmeImage
    }

    if ($wantAv) {
        # 音视频版：只嵌入 FFmpeg 载荷
        New-FfmpegPayload
        $exe = Publish-SingleFileExe -EmbedFfmpeg $true
        $payloadMb = [math]::Round((Get-Item $payloadPath).Length / 1MB, 1)
        Write-Host ("单文件 exe 生成：{0:N1} MB（含 {1} MB 内置 FFmpeg 载荷）" -f ((Get-Item $exe).Length / 1MB), $payloadMb)
        New-PortablePackage -ExePath $exe -PackageName 'RenamePro-av' -Readme $readmeAv
    }

    if ($wantDocs) {
        # 全功能版：FFmpeg 载荷 + 随包目录 LibreOffice\（文档引擎）
        New-FfmpegPayload
        if (-not (Test-Path (Join-Path $engineDir 'program\soffice.com'))) {
            if ($SkipEngineBuild) {
                Write-Host "错误：$engineDir 里没有 program\soffice.com，且指定了 -SkipEngineBuild" -ForegroundColor Red
                exit 1
            }
            Write-Host '未找到引擎树，运行 build-docengine.ps1…' -ForegroundColor Yellow
            & (Join-Path $root 'build-docengine.ps1')
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $engineDir 'program\soffice.com'))) {
                Write-Host '错误：无法生成随包引擎目录，全功能版无法打包' -ForegroundColor Red
                Write-Host '  请先运行：powershell -ExecutionPolicy Bypass -File setup-lo-dev.ps1   （展开官方 LibreOffice）' -ForegroundColor Yellow
                Write-Host '            powershell -ExecutionPolicy Bypass -File build-docengine.ps1  （裁剪成随包目录）' -ForegroundColor Yellow
                exit 1
            }
        }
        $exe = Publish-SingleFileExe -EmbedFfmpeg $true
        Write-Host ("单文件 exe 生成：{0:N1} MB（含内置 FFmpeg 载荷）" -f ((Get-Item $exe).Length / 1MB))
        New-DocsPackage -ExePath $exe -EngineDir $engineDir -PackageName 'RenamePro-full' -Readme $readmeDocs
    }

    # 汇总
    Write-Host '=== 打包完成 ===' -ForegroundColor Cyan
    Get-ChildItem $distDir -Filter '*.zip' | Sort-Object Name | ForEach-Object {
        Write-Host ("  {0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB))
    }
    if ($script:packageSummary.Count) {
        Write-Host ''
        $script:packageSummary | Format-Table -AutoSize | Out-String | Write-Host
    }
    Write-Host '提示：解压后双击 RenamePro.exe 即可使用（首次运行自动生成 config.json）。'
    Write-Host '      全功能版是目录包：RenamePro.exe 必须和同级 LibreOffice\ 一起使用。'
} finally {
    # 收尾：清理打包中间目录（载荷开关已由 MSBuild 控制，不再需要 .hold 藏文件）
    Remove-Item $stageDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "已清理打包中间目录：$stageDir"
}
