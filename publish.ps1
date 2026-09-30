# RenamePro 便携打包脚本：单文件自包含（不 Trim、无 ReadyToRun）+ 双包（完整版/图片版）+ 自动打 zip
# 用法：powershell -ExecutionPolicy Bypass -File publish.ps1 [-Mode Full|Image|Both] [-Version 1.0.0]
#
# 两个包的交付形态都是「一个 exe」：
#   完整版：ffmpeg/ffprobe 与运行时 DLL 被压成 Assets\ffmpeg-payload.zip 嵌入 exe，
#           首次用到音视频时由 Conversion\RuntimePayload.cs 解压到
#           %LOCALAPPDATA%\RenamePro\runtime\<载荷ID>\（见 README「单文件是怎么做到的」）
#   图片版：不嵌入载荷，音视频功能自动禁用
#
# 注意：两个包必须是「两次独立发布」——载荷是在编译期嵌入的，
#       同一次发布的产物不可能既带又不带载荷。
param(
    [ValidateSet('Full', 'Image', 'Both')]
    [string]$Mode = 'Both',
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'RenamePro.csproj'
$distDir = Join-Path $root 'dist'
$stageDir = Join-Path $root 'obj\publish-stage'
$ffmpegDir = Join-Path $root 'ffmpeg'
$payloadPath = Join-Path $root 'Assets\ffmpeg-payload.zip'
$payloadHold = "$payloadPath.hold"

$wantFull = $Mode -eq 'Full' -or $Mode -eq 'Both'
$wantImage = $Mode -eq 'Image' -or $Mode -eq 'Both'

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

四、日志
  程序目录 log.txt：每次转换的时间、旧路径 → 新路径、结果（OK/FAILED/CANCELLED/SKIPPED）与错误详情。
'@

$readmeFull = $readmeCommon + @'

五、音视频转换（本包已内置 ffmpeg）
  音视频转换所需的 ffmpeg 已内置在 RenamePro.exe 里，本目录只有这一个程序文件。
  首次进行音视频转换时会把内置的 ffmpeg 解压到：
      %LOCALAPPDATA%\RenamePro\runtime\<载荷ID>\
  只解压一次（约 1~2 秒），之后每次启动直接复用，属于正常行为。
  如果该目录被安全软件拦截，可把它加入白名单；解压失败时音视频功能会被禁用并写入 log.txt，
  图片转换不受影响。
'@

$readmeImage = $readmeCommon + @'

五、音视频转换（本轻量版不含 ffmpeg）
  本包体积最小，仅包含图片转换：改音视频后缀时会写日志并跳过（不弹任何提示）。
  需要音视频转换时，请改用完整版。
'@

# ---------------------------------------------------------------- 工具函数

# 生成 FFmpeg 载荷（把 ffmpeg\ 目录压成单个 zip，供 EmbeddedResource 嵌入）
function New-FfmpegPayload {
    $sources = Get-ChildItem $ffmpegDir -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -ne '.bak' -and $_.Name -notlike '*.hold' }
    if (-not $sources) {
        Write-Host "错误：$ffmpegDir 里没有 FFmpeg 文件，无法生成完整版" -ForegroundColor Red
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

# 把载荷临时移开（发布图片版用）
function Hide-Payload {
    if (Test-Path $payloadPath) {
        Remove-Item $payloadHold -Force -ErrorAction SilentlyContinue
        Move-Item $payloadPath $payloadHold -Force
    }
}

# 还原被移开的载荷
function Restore-Payload {
    if (Test-Path $payloadHold) {
        Remove-Item $payloadPath -Force -ErrorAction SilentlyContinue
        Move-Item $payloadHold $payloadPath -Force
    }
}

# 发布一次单文件 exe，返回 exe 路径
function Publish-SingleFileExe {
    Remove-Item $stageDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
    # Out-Host：让 dotnet 的输出直接落屏，避免被当成函数返回值混进 $exe
    dotnet publish $project -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishReadyToRun=false `
        -p:DebugType=none `
        -o $stageDir | Out-Host
    if ($LASTEXITCODE -ne 0) { Write-Host "发布失败（退出码 $LASTEXITCODE）" -ForegroundColor Red; exit $LASTEXITCODE }

    $exe = Join-Path $stageDir 'RenamePro.exe'
    if (-not (Test-Path $exe)) { Write-Host '未生成 RenamePro.exe' -ForegroundColor Red; exit 1 }
    return $exe
}

# 打包：包内只有 exe + 使用说明
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
    Compress-Archive -Path $folder -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host ("已生成：{0}（{1:N1} MB）" -f $zipPath, ((Get-Item $zipPath).Length / 1MB)) -ForegroundColor Green
}

# ---------------------------------------------------------------- 主流程
try {
    if ($wantFull) {
        # 完整版：先造载荷再发布（载荷在编译期嵌入）
        New-FfmpegPayload
        $exe = Publish-SingleFileExe
        $payloadMb = [math]::Round((Get-Item $payloadPath).Length / 1MB, 1)
        Write-Host ("单文件 exe 生成：{0:N1} MB（含 {1} MB 内置载荷）" -f ((Get-Item $exe).Length / 1MB), $payloadMb)
        New-PortablePackage -ExePath $exe -PackageName 'RenamePro-完整版' -Readme $readmeFull
    }

    if ($wantImage) {
        # 图片版：把载荷移开后重新发布一次，否则会被一起嵌进去
        Hide-Payload
        $exe = Publish-SingleFileExe
        Write-Host ("单文件 exe 生成：{0:N1} MB（不含 ffmpeg 载荷）" -f ((Get-Item $exe).Length / 1MB))
        New-PortablePackage -ExePath $exe -PackageName 'RenamePro-图片版' -Readme $readmeImage
    }

    # 汇总
    Write-Host '=== 打包完成 ===' -ForegroundColor Cyan
    Get-ChildItem $distDir -Filter '*.zip' | ForEach-Object {
        Write-Host ("  {0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB))
    }
    Write-Host '提示：解压后双击 RenamePro.exe 即可使用（首次运行自动生成 config.json）。'
} finally {
    # 收尾：还原被移开的载荷，清理打包中间目录
    Restore-Payload
    if (Test-Path $payloadHold) { Write-Host "警告：载荷仍留在 $payloadHold" -ForegroundColor Yellow }
    Remove-Item $stageDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "已清理打包中间目录：$stageDir"
}
