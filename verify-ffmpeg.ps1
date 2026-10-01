# RenamePro FFmpeg 包校验脚本
# 用法：powershell -ExecutionPolicy Bypass -File verify-ffmpeg.ps1
#       powershell -ExecutionPolicy Bypass -File verify-ffmpeg.ps1 -FromPayload
#
# 作用：换了 ffmpeg.exe / ffprobe.exe 之后，证明「支持矩阵没有缩水」。分三层：
#   1) 能力检查——AvConverter.cs 依赖的编码器、容器、滤镜是否都在
#   2) 转换冒烟——用 ffmpeg 自身合成素材，跑完重编码与流拷贝两类路径
#   3) 探测契约——ffprobe 的 JSON 与 ffmpeg 的 out_time_us 是否仍可被 AvConverter 解析
# 任一层失败都以非零码退出，便于在打包前拦住不合格的二进制。
#
# -FromPayload：不校验 ffmpeg\ 目录，而是先解包 Assets\ffmpeg-payload.zip（即会被嵌进
#   exe、运行时解压出来的那份），再对它跑全部检查，并核对载荷与 ffmpeg\ 目录是否一致。
param(
    [string]$FfmpegDir = '',
    [switch]$FromPayload
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$vendorDir = Join-Path $root 'ffmpeg'
$payloadPath = Join-Path $root 'Assets\ffmpeg-payload.zip'

$failures = 0
$passes = 0

function Pass([string]$text) { $script:passes++; Write-Host "  [OK]   $text" -ForegroundColor Green }
function Fail([string]$text) { $script:failures++; Write-Host "  [FAIL] $text" -ForegroundColor Red }

function Invoke-Native {
    # ffmpeg / ffprobe 把正常信息写到 stderr；PowerShell 5.1 在 $ErrorActionPreference='Stop'
    # 下会把原生程序的 stderr 当作终止错误，所以调用期间临时放行
    param([string]$Exe, [string[]]$Arguments)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { return (& $Exe @Arguments 2>&1) } finally { $ErrorActionPreference = $previous }
}

# 载荷模式：先把载荷解出来，校验的就是发布包里真正会被解压的那份
$payloadExtractDir = $null
if ($FromPayload) {
    if (-not (Test-Path $payloadPath)) {
        Write-Host "缺少载荷 $payloadPath（先跑 publish.ps1 -Mode Av 生成）" -ForegroundColor Red
        exit 1
    }
    $payloadExtractDir = Join-Path ([System.IO.Path]::GetTempPath()) 'RenamePro-payload-verify'
    if (Test-Path $payloadExtractDir) { Remove-Item $payloadExtractDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $payloadExtractDir | Out-Null
    Expand-Archive -Path $payloadPath -DestinationPath $payloadExtractDir -Force
    $FfmpegDir = $payloadExtractDir
    Write-Host ("=== 载荷模式：已解包 {0}（{1:N2} MB）===" -f $payloadPath, ((Get-Item $payloadPath).Length / 1MB)) -ForegroundColor Cyan
}

if (-not $FfmpegDir) { $FfmpegDir = $vendorDir }
$ffmpeg = Join-Path $FfmpegDir 'ffmpeg.exe'
$ffprobe = Join-Path $FfmpegDir 'ffprobe.exe'

if (-not (Test-Path $ffmpeg)) { Write-Host "缺少 $ffmpeg" -ForegroundColor Red; exit 1 }
if (-not (Test-Path $ffprobe)) { Write-Host "缺少 $ffprobe" -ForegroundColor Red; exit 1 }

Write-Host '=== 0. 二进制信息 ===' -ForegroundColor Cyan
foreach ($exe in @($ffmpeg, $ffprobe)) {
    $item = Get-Item $exe
    $ver = (Invoke-Native -Exe $exe -Arguments @('-hide_banner', '-version') | Select-Object -First 1)
    Write-Host ("  {0,-12} {1,7:N2} MB  {2}" -f $item.Name, ($item.Length / 1MB), $ver)
    Write-Host ("               SHA256 {0}" -f (Get-FileHash $exe -Algorithm SHA256).Hash)
}

# ---------------------------------------------------------------- 1. 能力检查
Write-Host '=== 1. 能力检查（编码器 / 容器 / 滤镜）===' -ForegroundColor Cyan
$encoderList = Invoke-Native -Exe $ffmpeg -Arguments @('-hide_banner', '-encoders')
$muxerList = Invoke-Native -Exe $ffmpeg -Arguments @('-hide_banner', '-muxers')
$demuxerList = Invoke-Native -Exe $ffmpeg -Arguments @('-hide_banner', '-demuxers')
$filterList = Invoke-Native -Exe $ffmpeg -Arguments @('-hide_banner', '-filters')

# 与 AvConverter.VideoEncodeArgs / AudioEncodeArgs 一一对应
$requiredEncoders = @('libx264', 'aac', 'libmp3lame', 'libvorbis', 'libvpx-vp9', 'libopus', 'flac', 'pcm_s16le', 'wmv2', 'wmav2')
foreach ($name in $requiredEncoders) {
    if ($encoderList | Select-String -Pattern ("\s" + [regex]::Escape($name) + "\s") -Quiet) { Pass "编码器 $name" } else { Fail "编码器 $name 缺失" }
}

# 与 AvConverter.ContainerCodecs / VideoExtensions / AudioExtensions 一一对应
$requiredContainers = @(
    @{ Name = 'mp4'; Mux = 'mp4'; Demux = 'mov,mp4,m4a,3gp,3g2,mj2' }
    @{ Name = 'matroska'; Mux = 'matroska'; Demux = 'matroska,webm' }
    @{ Name = 'webm'; Mux = 'webm'; Demux = 'matroska,webm' }
    @{ Name = 'avi'; Mux = 'avi'; Demux = 'avi' }
    @{ Name = 'asf(wmv)'; Mux = 'asf'; Demux = 'asf' }
    @{ Name = 'flv'; Mux = 'flv'; Demux = 'flv' }
    @{ Name = 'mpegts'; Mux = 'mpegts'; Demux = 'mpegts' }
    @{ Name = 'ogg'; Mux = 'ogg'; Demux = 'ogg' }
    @{ Name = 'mp3'; Mux = 'mp3'; Demux = 'mp3' }
    @{ Name = 'wav'; Mux = 'wav'; Demux = 'wav' }
    @{ Name = 'flac'; Mux = 'flac'; Demux = 'flac' }
    @{ Name = 'mov'; Mux = 'mov'; Demux = 'mov,mp4,m4a,3gp,3g2,mj2' }
)
foreach ($c in $requiredContainers) {
    $okMux = $muxerList | Select-String -Pattern ([regex]::Escape($c.Mux)) -Quiet
    $okDemux = $demuxerList | Select-String -Pattern ([regex]::Escape($c.Demux)) -Quiet
    if ($okMux -and $okDemux) { Pass "容器 $($c.Name)" }
    elseif (-not $okMux) { Fail "复用器 $($c.Mux) 缺失" }
    else { Fail "解复用器 $($c.Demux) 缺失" }
}

foreach ($name in @('null', 'anull', 'aresample', 'format', 'scale', 'testsrc2', 'sine')) {
    if ($filterList | Select-String -Pattern ("\s" + [regex]::Escape($name) + "\s") -Quiet) { Pass "滤镜 $name" } else { Fail "滤镜 $name 缺失" }
}

# ---------------------------------------------------------------- 素材准备
# 工作目录：优先 obj\（构建产物目录），被权限策略挡住时退回系统临时目录
$work = Join-Path $root 'obj\ffmpeg-verify'
try {
    if (Test-Path $work) { Remove-Item $work -Recurse -Force -ErrorAction Stop }
    New-Item -ItemType Directory -Force -Path $work -ErrorAction Stop | Out-Null
} catch {
    $work = Join-Path ([System.IO.Path]::GetTempPath()) 'RenamePro-ffmpeg-verify'
    if (Test-Path $work) { Remove-Item $work -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    Write-Host "  （obj\ 不可写，测试素材改用 $work）" -ForegroundColor DarkGray
}

function Invoke-Ffmpeg {
    param([string]$Description, [string[]]$Arguments)
    $all = @('-hide_banner', '-y') + $Arguments
    $output = Invoke-Native -Exe $ffmpeg -Arguments $all
    if ($LASTEXITCODE -eq 0) { Pass $Description } else {
        Fail "$Description（退出码 $LASTEXITCODE）"
        Write-Host ("         " + (($output | Select-Object -Last 4) -join ' / ')) -ForegroundColor DarkGray
    }
    return $output
}

Write-Host '=== 2. 转换冒烟 ===' -ForegroundColor Cyan
$srcMp4 = Join-Path $work 'src.mp4'
$srcWebm = Join-Path $work 'src.webm'
$srcFlac = Join-Path $work 'src.flac'
$srcOgg = Join-Path $work 'src.ogg'
$srcWav = Join-Path $work 'src.wav'

Invoke-Ffmpeg '生成 h264/aac mp4（重编码源）' @('-f', 'lavfi', '-i', 'testsrc2=size=320x240:rate=15:duration=3', '-f', 'lavfi', '-i', 'sine=frequency=440:duration=3', '-c:v', 'libx264', '-preset', 'ultrafast', '-crf', '35', '-c:a', 'aac', '-shortest', $srcMp4) | Out-Null
Invoke-Ffmpeg '生成 vp9/opus webm' @('-i', $srcMp4, '-c:v', 'libvpx-vp9', '-crf', '45', '-b:v', '0', '-c:a', 'libopus', $srcWebm) | Out-Null
Invoke-Ffmpeg '生成 flac 音频' @('-f', 'lavfi', '-i', 'sine=frequency=440:duration=3', '-c:a', 'flac', $srcFlac) | Out-Null
Invoke-Ffmpeg '生成 vorbis ogg 音频' @('-f', 'lavfi', '-i', 'sine=frequency=440:duration=3', '-c:a', 'libvorbis', $srcOgg) | Out-Null
Invoke-Ffmpeg '生成 pcm wav 音频' @('-f', 'lavfi', '-i', 'sine=frequency=440:duration=3', '-c:a', 'pcm_s16le', $srcWav) | Out-Null

# 流拷贝路径（AvPlanKind.StreamCopy：容器互转，秒级）
# mkv→mp4 复用上一步 mp4→mkv 的产物，不另建不存在的 src.mkv
$copyMkv = Join-Path $work 'copy.mkv'
Invoke-Ffmpeg 'mp4→mkv（流拷贝）' @('-i', $srcMp4, '-c', 'copy', $copyMkv) | Out-Null
Invoke-Ffmpeg 'mkv→mp4（流拷贝）' @('-i', $copyMkv, '-c', 'copy', (Join-Path $work 'copy.mp4')) | Out-Null
Invoke-Ffmpeg 'mp4→mov（流拷贝）' @('-i', $srcMp4, '-c', 'copy', (Join-Path $work 'copy.mov')) | Out-Null
Invoke-Ffmpeg 'mp4→ts（流拷贝）' @('-i', $srcMp4, '-c', 'copy', (Join-Path $work 'copy.ts')) | Out-Null
Invoke-Ffmpeg 'mp4→flv（流拷贝）' @('-i', $srcMp4, '-c', 'copy', (Join-Path $work 'copy.flv')) | Out-Null

# 视频重编码路径（AvPlanKind.VideoReencode 的各分支）
Invoke-Ffmpeg 'mp4→webm（libvpx-vp9+libopus）' @('-i', $srcMp4, '-c:v', 'libvpx-vp9', '-crf', '45', '-b:v', '0', '-c:a', 'libopus', (Join-Path $work 're.webm')) | Out-Null
Invoke-Ffmpeg 'mp4→avi（mpeg4+libmp3lame）' @('-i', $srcMp4, '-c:v', 'mpeg4', '-q:v', '8', '-c:a', 'libmp3lame', '-q:a', '7', (Join-Path $work 're.avi')) | Out-Null
Invoke-Ffmpeg 'mp4→wmv（wmv2+wmav2）' @('-i', $srcMp4, '-c:v', 'wmv2', '-b:v', '1M', '-c:a', 'wmav2', '-b:a', '128k', (Join-Path $work 're.wmv')) | Out-Null
Invoke-Ffmpeg 'webm→mp4（libx264+aac 默认分支）' @('-i', $srcWebm, '-c:v', 'libx264', '-crf', '35', '-preset', 'ultrafast', '-c:a', 'aac', (Join-Path $work 're.mp4')) | Out-Null

# 音频互转路径（AvPlanKind.AudioEncode 的各分支）
Invoke-Ffmpeg 'flac→mp3（libmp3lame）' @('-i', $srcFlac, '-vn', '-c:a', 'libmp3lame', '-q:a', '7', (Join-Path $work 'a.mp3')) | Out-Null
Invoke-Ffmpeg 'ogg→m4a（aac）' @('-i', $srcOgg, '-vn', '-c:a', 'aac', '-b:a', '128k', (Join-Path $work 'a.m4a')) | Out-Null
Invoke-Ffmpeg 'wav→flac（flac）' @('-i', $srcWav, '-vn', '-c:a', 'flac', (Join-Path $work 'a.flac')) | Out-Null
Invoke-Ffmpeg 'wav→ogg（libvorbis）' @('-i', $srcWav, '-vn', '-c:a', 'libvorbis', '-q:a', '5', (Join-Path $work 'a.ogg')) | Out-Null
Invoke-Ffmpeg 'mp4→mp3（提取音轨）' @('-i', $srcMp4, '-vn', '-c:a', 'libmp3lame', '-q:a', '7', (Join-Path $work 'extract.mp3')) | Out-Null

# ---------------------------------------------------------------- 3. 探测契约
Write-Host '=== 3. 探测契约（AvConverter.Probe / ReportProgress 依赖）===' -ForegroundColor Cyan

# 3a) ffprobe 结构化 JSON：字段名与类型必须与 AvConverter.Probe 的解析一致
$probeArgs = @('-v', 'error', '-show_entries', 'stream=codec_type,codec_name', '-show_entries', 'format=duration', '-of', 'json', $srcMp4)
$probeJson = Invoke-Native -Exe $ffprobe -Arguments $probeArgs
if ($LASTEXITCODE -ne 0) {
    Fail "ffprobe 退出码 $LASTEXITCODE"
} else {
    try {
        $doc = $probeJson | ConvertFrom-Json
        $streams = @($doc.streams)
        $types = $streams | ForEach-Object { $_.codec_type }
        $codecs = $streams | ForEach-Object { $_.codec_name }
        if ($types -contains 'video' -and $types -contains 'audio') { Pass 'ffprobe 报出 video+audio 流' } else { Fail "ffprobe 流类型异常：$($types -join ',')" }
        if ($codecs -contains 'h264' -and $codecs -contains 'aac') { Pass "ffprobe 报出编码名：$($codecs -join ',')" } else { Fail "ffprobe 编码名异常：$($codecs -join ',')" }
        $duration = [double]$doc.format.duration
        if ($duration -gt 1.0) { Pass ("ffprobe 时长可解析：{0:N2} s" -f $duration) } else { Fail "ffprobe 时长异常：$duration" }
    } catch {
        Fail "ffprobe JSON 解析失败：$($_.Exception.Message)"
    }
}

# 3b) 流拷贝判定所需的编码名，必须覆盖 webm/vp9 与 ogg/vorbis 这两类源
foreach ($pair in @(@{ File = $srcWebm; Codec = 'vp9' }, @{ File = $srcOgg; Codec = 'vorbis' })) {
    $out = Invoke-Native -Exe $ffprobe -Arguments @('-v', 'error', '-show_entries', 'stream=codec_name', '-of', 'json', $pair.File)
    if (($out -join '') -match $pair.Codec) { Pass "ffprobe 识别 $($pair.Codec)" } else { Fail "ffprobe 未能识别 $($pair.Codec)" }
}

# 3c) out_time_us：进度百分比换算的唯一来源
$progressOut = Invoke-Native -Exe $ffmpeg -Arguments @('-hide_banner', '-y', '-progress', 'pipe:1', '-i', $srcMp4, '-c:v', 'libx264', '-preset', 'ultrafast', '-crf', '40', '-c:a', 'aac', (Join-Path $work 'progress.mp4'))
if (($progressOut -join "`n") -match 'out_time_us=\d+') { Pass 'out_time_us 可用（进度条换算依赖）' } else { Fail 'out_time_us 缺失，进度条将失效' }

Invoke-Ffmpeg 'ffmpeg -f null -（探测副路径）' @('-i', $srcMp4, '-f', 'null', '-') | Out-Null

# ---------------------------------------------------------------- 4. 载荷一致性（仅 -FromPayload）
if ($FromPayload) {
    Write-Host '=== 4. 载荷一致性（载荷 vs ffmpeg\ 目录）===' -ForegroundColor Cyan

    # 必需清单必须与 RuntimePayload.cs 的 PayloadFiles 一致
    $required = @(
        'ffmpeg.exe', 'ffprobe.exe',
        'libx264-165.dll', 'libmp3lame-0.dll', 'libopus-0.dll', 'libvorbis-0.dll',
        'libvorbisenc-2.dll', 'libogg-0.dll', 'libvpx-1.dll',
        'libiconv-2.dll', 'libwinpthread-1.dll', 'zlib1.dll'
    )
    foreach ($name in $required) {
        if (Test-Path (Join-Path $payloadExtractDir $name)) { Pass "载荷含 $name" } else { Fail "载荷缺少 $name" }
    }

    # 载荷与 ffmpeg\ 目录必须完全一致（防止改了目录忘了重新生成载荷，或反之）
    $payloadFiles = Get-ChildItem $payloadExtractDir -File | Sort-Object Name
    $vendorFiles = if (Test-Path $vendorDir) { Get-ChildItem $vendorDir -File | Where-Object { $_.Extension -ne '.bak' } | Sort-Object Name } else { @() }

    $onlyInPayload = $payloadFiles.Name | Where-Object { -not ($vendorFiles.Name -contains $_) }
    $onlyInVendor = $vendorFiles.Name | Where-Object { -not ($payloadFiles.Name -contains $_) }
    if ($onlyInPayload) { Fail ("载荷多出文件：" + ($onlyInPayload -join '、')) } else { Pass '载荷无多余文件' }
    if ($onlyInVendor) { Fail ("ffmpeg\ 目录多出文件（未进载荷）：" + ($onlyInVendor -join '、')) } else { Pass 'ffmpeg\ 目录无遗漏文件' }

    foreach ($file in $payloadFiles) {
        $counterpart = Join-Path $vendorDir $file.Name
        if (-not (Test-Path $counterpart)) { continue }
        $h1 = (Get-FileHash $file.FullName -Algorithm SHA256).Hash
        $h2 = (Get-FileHash $counterpart -Algorithm SHA256).Hash
        if ($h1 -eq $h2) { Pass "SHA256 一致：$($file.Name)" } else { Fail "SHA256 不一致：$($file.Name)" }
    }
}

# ---------------------------------------------------------------- 汇总
Write-Host '=== 汇总 ===' -ForegroundColor Cyan
Write-Host ("  通过 {0} 项，失败 {1} 项" -f $passes, $failures) -ForegroundColor $(if ($failures -eq 0) { 'Green' } else { 'Red' })
Write-Host ("  测试素材目录：$work")
if ($failures -gt 0) {
    Write-Host '校验未通过：不要用这组二进制打包。回到 build-ffmpeg.ps1 白名单补组件后重编。' -ForegroundColor Red
    exit 1
}
Write-Host '校验通过：可以执行 publish.ps1 -Mode All 重新打包。' -ForegroundColor Green
exit 0
