# RenamePro 便携打包脚本：单文件自包含（不 Trim、无 ReadyToRun）+ 双包（完整版/图片版）+ 自动打 zip
# 用法：powershell -ExecutionPolicy Bypass -File publish.ps1 [-Mode Full|Image|Both] [-Version 1.0.0]
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

Write-Host "=== RenamePro 便携打包（v$Version，模式 $Mode）===" -ForegroundColor Cyan

# 1) 清理输出
Remove-Item $distDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $stageDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $distDir, $stageDir | Out-Null

# 2) 单文件自包含发布
#    关键点：PublishReadyToRun=false（体积减半）、不使用 PublishTrim（会破坏 COM/反射）
#    ffmpeg/ffprobe 作为内容文件留在 exe 旁边（规格要求以 exe 形式放在程序目录）
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishReadyToRun=false `
    -p:DebugType=none `
    -o $stageDir

if ($LASTEXITCODE -ne 0) { Write-Host "发布失败（退出码 $LASTEXITCODE）" -ForegroundColor Red; exit $LASTEXITCODE }

$exePath = Join-Path $stageDir 'RenamePro.exe'
if (-not (Test-Path $exePath)) { Write-Host "未生成 RenamePro.exe" -ForegroundColor Red; exit 1 }
$exeSize = [math]::Round((Get-Item $exePath).Length / 1MB, 1)
Write-Host "单文件 exe 生成：$exeSize MB"

# 3) 使用说明（每个包各一份，写入时用 UTF-8 带 BOM，记事本不乱码）
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

五、音视频转换（本包已含 ffmpeg）
  本目录已附带 ffmpeg.exe / ffprobe.exe，音视频改名可直接转换。
  如需进一步减小体积，可替换为更小的 ffmpeg 构建（需含 libx264 / aac / libmp3lame / libvorbis / libvpx / flac）。
'@

$readmeImage = $readmeCommon + @'

五、音视频转换（本轻量版不含 ffmpeg）
  本包体积最小，仅包含图片转换：改音视频后缀时会写日志并跳过（不弹任何提示）。
  需要音视频转换时，把 ffmpeg.exe 与 ffprobe.exe 放进本目录即可，无需任何配置。
'@

# 4) 打包函数：组织包内文件并压缩为 zip
function New-PortablePackage {
    param(
        [string]$PackageName,
        [bool]$WithMedia,
        [string]$Readme
    )
    $folder = Join-Path $stageDir $PackageName
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    Copy-Item $exePath (Join-Path $folder 'RenamePro.exe')
    if ($WithMedia) {
        Copy-Item (Join-Path $stageDir 'ffmpeg.exe') $folder
        Copy-Item (Join-Path $stageDir 'ffprobe.exe') $folder
    }
    [System.IO.File]::WriteAllText((Join-Path $folder '使用说明.txt'), $Readme, (New-Object System.Text.UTF8Encoding($true)))

    $zipPath = Join-Path $distDir ($PackageName + "-v$Version.zip")
    Compress-Archive -Path $folder -DestinationPath $zipPath -CompressionLevel Fastest
    Write-Host ("已生成：{0}（{1:N1} MB）" -f $zipPath, ((Get-Item $zipPath).Length / 1MB)) -ForegroundColor Green
}

# 5) 按模式打包
if ($Mode -eq 'Full' -or $Mode -eq 'Both') {
    if (-not (Test-Path (Join-Path $stageDir 'ffmpeg.exe'))) {
        Write-Host "警告：发布目录缺少 ffmpeg.exe，完整版将不含音视频功能" -ForegroundColor Yellow
    }
    New-PortablePackage -PackageName 'RenamePro-完整版' -WithMedia $true -Readme $readmeFull
}
if ($Mode -eq 'Image' -or $Mode -eq 'Both') {
    New-PortablePackage -PackageName 'RenamePro-图片版' -WithMedia $false -Readme $readmeImage
}

# 6) 汇总
Write-Host "=== 打包完成 ===" -ForegroundColor Cyan
Get-ChildItem $distDir -Filter '*.zip' | ForEach-Object {
    Write-Host ("  {0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB))
}
Write-Host "提示：解压后双击 RenamePro.exe 即可使用（首次运行自动生成 config.json）。"

# 7) 清理打包中间目录（含包内文件副本，约 900MB，可随时重新生成）
Remove-Item $stageDir -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "已清理打包中间目录：$stageDir"

