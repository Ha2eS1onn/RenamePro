# RenamePro 精简版 FFmpeg 构建脚本
# 用法：powershell -ExecutionPolicy Bypass -File build-ffmpeg.ps1 [-Version 9.0.2] [-Jobs 8] [-Force]
#
# 为什么自己编：官方构建（gyan.dev essentials / full、BtbN）都含有本程序用不到的大量组件与全部硬件
# 加速入口，ffmpeg.exe + ffprobe.exe 解出来就是 201 MB。这里用 --disable-everything 做白名单，
# 只保留 AvConverter.cs 实际会调用的编码器、解码器、复用器与协议。
#
# 关键约束（改动白名单前务必先读）：
#   1) 解码器必须覆盖「输入侧」格式集合，不只是「输出侧」编码器集合：
#      代码会对 xvid/mpeg4 的 avi、hevc 的 mkv、vp9 的 webm 等源做重编码，
#      所以 mpeg4 / vc1 / wmv3 / hevc / vp9 等解码器是必需的。
#   2) AvConverter 依赖 -progress pipe:1 输出里的 out_time_us，进度条靠它换算百分比。
#   3) ffprobe 与 ffmpeg 共用同一份静态库，去掉 ffprobe 省不下多少；本项目保留它，
#      因为 AvConverter.Probe 解析的是 ffprobe 的结构化 JSON（详见 README）。
#   4) 任何新增格式需求，都要回到本脚本的 configure 开关里补组件，并跑 verify-ffmpeg.ps1。
#
# 产物：覆盖 ffmpeg\ffmpeg.exe 与 ffmpeg\ffprobe.exe（不入库，仅用于本地打包）。
param(
    [string]$Version = '9.0.2',
    [int]$Jobs = 0,
    [switch]$Force,
    [string]$Proxy = '',
    # 编译工作区。默认放在 MSYS2 自带的缓存目录：仓库里的 obj\ 有时会被文件策略挡住，
    # 而源码树与中间产物没必要留在仓库里。想放到别处就传 -WorkRoot。
    [string]$WorkRoot = ''
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$vendorDir = Join-Path $root 'ffmpeg'
$msysRoot = @('C:\msys64', "$env:LOCALAPPDATA\Programs\msys64") |
    Where-Object { Test-Path (Join-Path $_ 'usr\bin\bash.exe') } | Select-Object -First 1
if (-not $WorkRoot) {
    if ($msysRoot) { $WorkRoot = Join-Path $msysRoot 'var\cache\renamepro\ffmpeg-build' }
    else { $WorkRoot = Join-Path $env:TEMP 'renamepro-ffmpeg-build' }
}
$srcDir = Join-Path $WorkRoot 'src'
$installDir = Join-Path $WorkRoot 'install'
$scriptPath = Join-Path $WorkRoot 'build.sh'

# 1) 定位 MSYS2（UCRT64 工具链，与上游构建一致）
$msysCandidates = @('C:\msys64', "$env:LOCALAPPDATA\Programs\msys64")
$msysRoot = $msysCandidates | Where-Object { Test-Path (Join-Path $_ 'usr\bin\bash.exe') } | Select-Object -First 1
if (-not $msysRoot) {
    Write-Host "未找到 MSYS2，请先安装：" -ForegroundColor Red
    Write-Host '  winget install --id MSYS2.MSYS2 -e --accept-package-agreements --accept-source-agreements'
    Write-Host '安装后在 MSYS2 UCRT64 里执行（或让本脚本用 -Proxy 走代理）：'
    Write-Host '  pacman -S --needed --noconfirm base-devel mingw-w64-ucrt-x86_64-gcc \'
    Write-Host '    mingw-w64-ucrt-x86_64-binutils mingw-w64-ucrt-x86_64-nasm \'
    Write-Host '    mingw-w64-ucrt-x86_64-zlib mingw-w64-ucrt-x86_64-libiconv \'
    Write-Host '    git make diffutils pkgconf'
    exit 1
}
$bash = Join-Path $msysRoot 'usr\bin\bash.exe'
Write-Host "MSYS2: $msysRoot" -ForegroundColor Cyan
Write-Host "工作区: $WorkRoot" -ForegroundColor Cyan

if ($Jobs -le 0) { $Jobs = [Math]::Max(2, [Environment]::ProcessorCount) }

# 代理：只影响本次进程内的 git / bash 子进程
if ($Proxy) {
    $env:http_proxy = $Proxy; $env:https_proxy = $Proxy
    $env:HTTP_PROXY = $Proxy; $env:HTTPS_PROXY = $Proxy
    $env:RENAMEPRO_PROXY = $Proxy
    Write-Host "使用代理: $Proxy" -ForegroundColor Cyan
}

# 2) 准备源码
New-Item -ItemType Directory -Force -Path $WorkRoot, $installDir | Out-Null
if (-not (Test-Path (Join-Path $srcDir 'configure'))) {
    if (Test-Path $srcDir) { Remove-Item $srcDir -Recurse -Force }
    Write-Host "克隆 FFmpeg $Version 源码…" -ForegroundColor Cyan
    & git clone --depth 1 --branch "n$Version" https://git.ffmpeg.org/ffmpeg.git $srcDir
    if ($LASTEXITCODE -ne 0) {
        Write-Host "标签 n$Version 克隆失败，改用官方 GitHub 镜像" -ForegroundColor Yellow
        Remove-Item $srcDir -Recurse -Force -ErrorAction SilentlyContinue
        & git clone --depth 1 --branch "n$Version" https://github.com/FFmpeg/FFmpeg.git $srcDir
    }
    if ($LASTEXITCODE -ne 0) { Write-Host '源码克隆失败（网络不可达？）' -ForegroundColor Red; exit 1 }
} else {
    Write-Host "复用已有源码：$srcDir" -ForegroundColor Cyan
}

# 3) 白名单 configure（单行，避免续行符被 CRLF 破坏）
$configureFlags = @(
    # FFmpeg 没有 --toolchain 开关（那是别的构建体系的），编译器用 --cc/--cxx 显式指定
    '--arch=x86_64', '--target-os=mingw32', '--cc=gcc', '--cxx=g++'
    '--enable-gpl', '--enable-version3', '--enable-static', '--disable-shared'
    '--disable-autodetect', '--disable-everything', '--disable-doc', '--disable-debug'
    # 注：FFmpeg 9.0 已移除 libpostproc，因此没有 --disable-postproc / --disable-libpostproc 这类开关
    # 注：不写 --disable-avdevice —— 不适用；lavfi（测试素材源）需要 avfilter，
    # 而 lavfi_indev_deps="avfilter" 在 FFmpeg 9.0 里被改成了 avdevice 依赖链，
    # 所以这里保留 avdevice 不关（设备本身仍然全部关闭）
    '--disable-network', '--disable-devices'
    '--disable-hwaccels', '--disable-vaapi', '--disable-vdpau', '--disable-vulkan'
    '--disable-cuda-llvm', '--disable-d3d11va', '--disable-dxva2', '--disable-d3d12va'
    # 协议：本地文件与管道
    '--enable-protocol=file,pipe'
    # lavfi：仅为 verify-ffmpeg.ps1 自造测试素材（testsrc2 / sine）而保留，运行时用不到；
    # 去掉它体积几乎不变，但校验脚本就没法生成素材了
    '--enable-indev=lavfi'
    # 解复用：ContainerCodecs 覆盖的输入容器 + 音频容器 + 测试素材生成用的裸流
    '--enable-demuxer=mov,matroska,avi,asf,flv,mpegts,ogg,mp3,wav,flac,aac,image2'
    # 复用：VideoExtensions / AudioExtensions 的全部目标容器
    # ipod 是 .m4a 的复用器（FFmpeg 没有叫 m4a 的复用器）；null 供 -f null - 探测路径使用
    '--enable-muxer=mp4,mov,matroska,webm,avi,asf,flv,mpegts,ogg,mp3,wav,flac,image2,ipod,null'
    # 解码器：输入侧格式全集（重编码路径必需）
    # 注意 jpeg 在 FFmpeg 里的组件名是 mjpeg（jpg 由它解码），写 jpeg 会被 configure 拒绝
    # wrapped_avframe 是 lavfi 视频源（testsrc2）输出帧所用的解码器，测试素材生成依赖它
    '--enable-decoder=h264,hevc,mpeg4,mpeg2video,vp8,vp9,av1,wmv3,vc1,mjpeg,png,gif,webp,aac,ac3,mp3,opus,vorbis,flac,pcm_s16le,wmav2,wmapro,alac,wrapped_avframe'
    # 编码器：AvConverter.VideoEncodeArgs / AudioEncodeArgs 用到的全集
    # libvpx_vp9 / libvpx_vp8 必须显式列出：--enable-libvpx 只打开库本身，
    # --disable-everything 之后它的编码器组件默认是关的，不列就编不出 libvpx-vp9
    # wrapped_avframe 编码器供 -f null - 使用（默认编码器，不列会报 Encoder not found）
    '--enable-encoder=libx264,aac,libmp3lame,libvorbis,libopus,flac,pcm_s16le,wmv2,wmav2,libvpx_vp9,libvpx_vp8,mpeg4,wrapped_avframe'
    # 解析器：容器内裸流探测所需
    '--enable-parser=h264,hevc,mpeg4video,mpegvideo,vp8,vp9,av1,mjpeg,png,aac,ac3,opus,vorbis,flac,mpegaudio'
    # 滤镜：null 输出（探测用）+ 重采样/像素格式（重编码路径必需）+ 三个测试源（verify-ffmpeg.ps1 依赖）
    '--enable-filter=null,anull,format,aformat,aresample,scale,testsrc2,sine,color'
    # 外部库：只留编码器与压缩/编码转换
    '--enable-libx264', '--enable-libmp3lame', '--enable-libvorbis', '--enable-libopus', '--enable-libvpx'
    '--enable-zlib', '--enable-iconv'
    # 静态链接外部库：不这样处理，产物会依赖 libx264-165.dll / libiconv-2.dll / zlib1.dll 等
    # 8 个 DLL（exe 旁边得凑齐一整套）。
    # 关键在顺序：链接行是 LDFLAGS → OBJS → 各静态库(.a) → pkg-config 来的 -lxxx → EXTRALIBS，
    # 所以 -Wl,-Bstatic 放 LDFLAGS 开头、-Wl,-Bdynamic 必须放 LDFLAGS 末尾，
    # 这样中间那些 -lx264/-lmp3lame/... 才落在静态段内。值里含空格，必须自带引号。
    '"--extra-ldflags=-Wl,-Bstatic -Wl,-Bdynamic"'
    # libiconv 放 extra-libs：它位于链接行更靠后的位置，能命中静态段
    '--extra-libs=-liconv'
    # 注：mingw 目标默认用 win32 线程，写 --enable-pthreads 会让 configure 直接失败
) -join ' '

# Windows 路径 → MSYS2 的 /c/... 形式（bash 与 make 只认这一种）
function ConvertTo-UnixPath([string]$path) {
    $p = $path -replace '\\', '/'
    if ($p -match '^([A-Za-z]):') { $p = '/' + $Matches[1].ToLowerInvariant() + $p.Substring(2) }
    return $p
}
$srcUnix = ConvertTo-UnixPath $srcDir
$installUnix = ConvertTo-UnixPath $installDir

$bashScript = @"
set -e
export MSYSTEM=UCRT64
export CHERE_INVOKING=1
export PATH=/ucrt64/bin:/usr/bin:`$PATH
# UCRT64 的 .pc 都在 /ucrt64/lib/pkgconfig；不设这个变量 pkg-config 找不到 x264/opus 等
export PKG_CONFIG_PATH=/ucrt64/lib/pkgconfig:/ucrt64/share/pkgconfig
# 静态链接：UCRT64 的 x264/lame/opus/vorbis/vpx/iconv 默认走导入库，
# 不处理的话产物会依赖 libx264-165.dll 等 8 个 DLL，exe 旁边得凑一整套。
# 注意：直接 export LDFLAGS 无效——FFmpeg 会把库参数放在 -Bstatic 之前；
# 必须用 --extra-cflags / --extra-ldflags 走 FFmpeg 自己的注入点。
export CFLAGS="-O2"
export CXXFLAGS="-O2"
# 本机走本地代理出网时（例如 7897 端口的 Clash），用 -Proxy 参数把代理透给 git / make
if [ -n "`$RENAMEPRO_PROXY" ]; then
  export http_proxy="`$RENAMEPRO_PROXY"
  export https_proxy="`$RENAMEPRO_PROXY"
fi
cd "$srcUnix"
echo "=== configure ==="
./configure --prefix="$installUnix" $configureFlags
echo "=== make ==="
make -j$Jobs
make install
echo "=== strip ==="
strip --strip-unneeded "$installUnix/bin/ffmpeg.exe" || true
strip --strip-unneeded "$installUnix/bin/ffprobe.exe" || true
"@

# 写入时统一 LF，避免 bash 吃到 CR
$bashScript = $bashScript -replace "`r`n", "`n"
[System.IO.File]::WriteAllText($scriptPath, $bashScript, (New-Object System.Text.UTF8Encoding($false)))

# 4) 执行构建（不加 --login：避免 bash 切到 HOME 导致相对路径失效；环境由脚本自己 export）
Write-Host "开始构建（并行 $Jobs，源码 $srcDir）…首次编译 30~90 分钟" -ForegroundColor Cyan
& $bash -c "'$(ConvertTo-UnixPath $scriptPath)'"
if ($LASTEXITCODE -ne 0) { Write-Host "构建失败（退出码 $LASTEXITCODE）" -ForegroundColor Red; exit $LASTEXITCODE }

# 5) 落到 ffmpeg\ 目录
$copied = @()
foreach ($name in @('ffmpeg.exe', 'ffprobe.exe')) {
    $built = Join-Path (Join-Path $installDir 'bin') $name
    if (-not (Test-Path $built)) { Write-Host "缺少产物 $built" -ForegroundColor Red; exit 1 }
    $target = Join-Path $vendorDir $name
    if ((Test-Path $target) -and -not $Force) {
        Copy-Item $target "$target.bak" -Force
        Write-Host "已备份原文件：$target.bak（确认无误后可删除）" -ForegroundColor Yellow
    }
    try {
        Copy-Item $built $target -Force -ErrorAction Stop
    } catch {
        Write-Host "无法写入 $target" -ForegroundColor Red
        Write-Host "  产物已就绪：$built" -ForegroundColor Yellow
        Write-Host "  若是受限文件策略导致，请在受限环境之外重跑本脚本，或手工把上面两个文件复制到 $vendorDir" -ForegroundColor Yellow
        exit 1
    }
    $sizeMb = [math]::Round((Get-Item $target).Length / 1MB, 2)
    $hash = (Get-FileHash $target -Algorithm SHA256).Hash
    Write-Host ("  {0,-12} {1,7:N2} MB  SHA256 {2}" -f $name, $sizeMb, $hash) -ForegroundColor Green
    $copied += $sizeMb
}

# 6) 复制运行时依赖的 DLL
#    这两个 exe 不是完全静态的：UCRT64 的 x264/lame/opus/vorbis/vpx/iconv/zlib 走的是导入库，
#    所以 exe 旁边必须放齐这些 DLL，否则程序双击后静默失败（退出码 0xC0000135）。
#    清单用 `ldd ffmpeg.exe` 实测得出，包含传递依赖 libvpx-1 / libogg-0，共约 5.3 MB。
$dllSource = Join-Path $msysRoot 'ucrt64\bin'
$dllNames = @(
    'libx264-165.dll', 'libmp3lame-0.dll', 'libopus-0.dll', 'libvorbis-0.dll',
    'libvorbisenc-2.dll', 'libogg-0.dll', 'libvpx-1.dll',
    'libiconv-2.dll', 'libwinpthread-1.dll', 'zlib1.dll'
)
Write-Host '--- 运行时 DLL ---' -ForegroundColor Cyan
foreach ($name in $dllNames) {
    $src = Join-Path $dllSource $name
    if (-not (Test-Path $src)) {
        Write-Host "  警告：$src 不存在，请核对 UCRT64 里该库的版本号后缀（如 libx264-NNN.dll）" -ForegroundColor Yellow
        continue
    }
    Copy-Item $src $vendorDir -Force
    $sizeMb = [math]::Round((Get-Item (Join-Path $vendorDir $name)).Length / 1MB, 2)
    Write-Host ("  {0,-24} {1,7:N2} MB" -f $name, $sizeMb) -ForegroundColor Green
}

Write-Host '=== 构建完成 ===' -ForegroundColor Cyan
Write-Host '下一步：powershell -ExecutionPolicy Bypass -File verify-ffmpeg.ps1'
Write-Host '校验通过后再执行：powershell -ExecutionPolicy Bypass -File publish.ps1 -Mode All'
