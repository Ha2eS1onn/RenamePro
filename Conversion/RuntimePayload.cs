using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using RenamePro.Core;

namespace RenamePro.Conversion;

/// <summary>
/// FFmpeg 载荷的运行时管理：把编译期嵌入程序集的 ffmpeg/ffprobe 及运行时 DLL 惰性解压到
/// 用户可写目录，并把该目录提供给 AvConverter 作为子进程调用位置。
///
/// 为什么必须解压而不能直接从资源里调用：
///   Windows 加载器只按磁盘路径加载模块。ffmpeg.exe 是子进程（CreateProcess），
///   它依赖的 10 个 DLL 要由加载器在 exe 同目录搜索，嵌入资源对加载器不可见。
///
/// 目录策略：
///   首选 %LOCALAPPDATA%\RenamePro\runtime\&lt;载荷ID&gt;\（不会被清理工具误删，便于加白名单）；
///   不可写时退回 %TEMP%\RenamePro\runtime\&lt;载荷ID&gt;\；
///   开发期若程序目录已经放好了 ffmpeg.exe / ffprobe.exe，直接用程序目录，不走解压。
///
/// 载荷 ID 由载荷字节的 SHA256 前 16 位决定：换了 FFmpeg 就换目录，不会新旧 DLL 混用。
/// </summary>
internal static class RuntimePayload
{
    /// <summary>嵌入资源名（与 RenamePro.csproj 里的 LogicalName 一致）。</summary>
    private const string ResourceName = "RenamePro.Assets.ffmpeg-payload.zip";

    /// <summary>标记文件名：写完代表该目录完整可用。</summary>
    private const string MarkerFileName = ".payload-complete";

    /// <summary>载荷必须包含的文件（与 build-ffmpeg.ps1 的 DLL 清单一致）。</summary>
    private static readonly string[] PayloadFiles =
    {
        "ffmpeg.exe", "ffprobe.exe",
        "libx264-165.dll", "libmp3lame-0.dll", "libopus-0.dll", "libvorbis-0.dll",
        "libvorbisenc-2.dll", "libogg-0.dll", "libvpx-1.dll",
        "libiconv-2.dll", "libwinpthread-1.dll", "zlib1.dll"
    };

    /// <summary>首次访问时解压一次；同一进程内只解一次。</summary>
    private static readonly Lazy<string> PayloadDirectoryLazy = new(
        ResolvePayloadDirectory, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>载荷包含的文件清单（只读，供校验脚本与自检使用）。</summary>
    public static IReadOnlyList<string> RequiredFiles => PayloadFiles;

    /// <summary>本程序集是否带载荷（图片版不带，属正常情况而非错误）。</summary>
    public static bool IsBundled => GetPayloadStream() != null;

    /// <summary>
    /// 确保载荷可用并返回其目录。会触发解压；失败抛 <see cref="InvalidOperationException"/>，
    /// 消息里带可直接写进日志的具体原因。
    /// </summary>
    public static string EnsureAvailable() => PayloadDirectoryLazy.Value;

    /// <summary>
    /// 只查询缓存状态，不触发解压。返回值表示"此刻已经有可用目录"。
    /// </summary>
    public static bool TryGetCachedDirectory(out string? directory)
    {
        directory = null;
        // Lazy 已求值（成功）时直接给出
        if (PayloadDirectoryLazy.IsValueCreated)
        {
            directory = PayloadDirectoryLazy.Value;
            return true;
        }
        // 未求值：看看开发期目录或已解压缓存是否已就绪
        var dev = GetDevelopmentDirectory();
        if (dev != null) { directory = dev; return true; }
        return false;
    }

    /// <summary>
    /// 决定使用哪个目录：开发期目录 → 已解压缓存 → 现场解压。
    /// </summary>
    private static string ResolvePayloadDirectory()
    {
        // 开发期：程序目录已经有 ffmpeg.exe / ffprobe.exe，直接用（dotnet build 输出目录）
        var dev = GetDevelopmentDirectory();
        if (dev != null) return dev;

        using var payload = GetPayloadStream();
        if (payload == null)
        {
            throw new InvalidOperationException(
                "本 exe 未内置 FFmpeg 载荷（图片版），音视频转换不可用");
        }

        var payloadId = ComputePayloadId(payload);
        var target = Path.Combine(GetRuntimeRoot(), payloadId);

        if (IsComplete(target))
        {
            LogPayload($"复用已解压载荷：{target}");
            return target;
        }

        // 可能上次解压被中断（标记文件缺失），清掉重来
        if (Directory.Exists(target))
        {
            TryDeleteDirectory(target);
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Extract(payload, target);
        watch.Stop();
        // 实际落点可能是 %TEMP%（首选目录不可写时），以 IsComplete 的结果为准
        var actual = IsComplete(target) ? target : FindUsableTarget(target, payloadId);
        LogPayload($"载荷已解压：{actual}（{watch.ElapsedMilliseconds} ms）");
        CleanupOldPayloads(actual);
        return actual;
    }

    /// <summary>首选目录没写成时，找出实际可用的那个解压目录。</summary>
    private static string FindUsableTarget(string preferred, string payloadId)
    {
        if (IsComplete(preferred)) return preferred;
        var alternative = Path.Combine(Path.GetTempPath(), "RenamePro", "runtime", payloadId);
        if (IsComplete(alternative)) return alternative;
        throw new InvalidOperationException($"载荷解压后未找到完整目录：{preferred}");
    }

    /// <summary>开发期目录：程序目录里同时存在两个 exe 时视为开发者已自备。</summary>
    private static string? GetDevelopmentDirectory()
    {
        var baseDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(baseDir, "ffmpeg.exe")) &&
            File.Exists(Path.Combine(baseDir, "ffprobe.exe")))
        {
            return baseDir;
        }
        return null;
    }

    /// <summary>读取嵌入的载荷流；未嵌入时返回 null（调用方负责释放）。</summary>
    private static Stream? GetPayloadStream() =>
        typeof(RuntimePayload).Assembly.GetManifestResourceStream(ResourceName);

    /// <summary>载荷 ID：字节内容的 SHA256 前 16 位。</summary>
    private static string ComputePayloadId(Stream payload)
    {
        var position = payload.Position;
        payload.Position = 0;
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(payload);
        payload.Position = position;
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>运行时根目录：%LOCALAPPDATA% 优先，不可用则退回 %TEMP%。</summary>
    private static string GetRuntimeRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            return Path.Combine(localAppData, "RenamePro", "runtime");
        }
        return Path.Combine(Path.GetTempPath(), "RenamePro", "runtime");
    }

    /// <summary>目录是否已完整解压（标记文件 + 必需文件齐全非空）。</summary>
    private static bool IsComplete(string directory)
    {
        if (!File.Exists(Path.Combine(directory, MarkerFileName))) return false;
        foreach (var name in PayloadFiles)
        {
            var file = new FileInfo(Path.Combine(directory, name));
            if (!file.Exists || file.Length == 0) return false;
        }
        return true;
    }

    /// <summary>
    /// 解压载荷到目标目录。先写临时目录，成功后整体 Move，降低"解压到一半被杀"的残留面。
    /// 写入失败时按顺序尝试两个根目录。
    /// </summary>
    private static void Extract(Stream payload, string target)
    {
        var candidates = new List<string> { target };
        // 首选根目录写不下去时，追加 %TEMP% 作为备选
        var tempRoot = Path.Combine(Path.GetTempPath(), "RenamePro", "runtime");
        var altTarget = Path.Combine(tempRoot, Path.GetFileName(target));
        if (!string.Equals(target, altTarget, StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(altTarget);
        }

        Exception? lastError = null;
        foreach (var candidate in candidates)
        {
            try
            {
                ExtractTo(payload, candidate);
                if (!string.Equals(candidate, target, StringComparison.OrdinalIgnoreCase))
                {
                    LogPayload($"首选目录不可写，已改解压到 {candidate}");
                }
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException(
            $"FFmpeg 载荷解压失败（已尝试 {string.Join("、", candidates)}）：{lastError?.Message}", lastError);
    }

    /// <summary>真正的解压动作：临时目录 → 标记 → 改名到位。</summary>
    private static void ExtractTo(Stream payload, string target)
    {
        var parent = Path.GetDirectoryName(target)
                     ?? throw new InvalidOperationException($"载荷目录无父目录：{target}");
        Directory.CreateDirectory(parent);

        var staging = Path.Combine(parent, $".tmp-{Path.GetFileName(target)}-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            payload.Position = 0;
            using (var archive = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true))
            {
                foreach (var entry in archive.Entries)
                {
                    // 载荷是平铺结构，只取文件名，防目录穿越
                    var name = Path.GetFileName(entry.FullName);
                    if (string.IsNullOrEmpty(name)) continue;
                    entry.ExtractToFile(Path.Combine(staging, name), overwrite: true);
                }
            }

            // 完整性确认：必需文件齐全非空，再落标记
            foreach (var name in PayloadFiles)
            {
                var file = new FileInfo(Path.Combine(staging, name));
                if (!file.Exists || file.Length == 0)
                {
                    throw new InvalidOperationException($"载荷缺少文件或内容为空：{name}");
                }
            }
            File.WriteAllText(Path.Combine(staging, MarkerFileName), DateTime.UtcNow.ToString("O"));

            // 目标可能已被并发进程建好：那就丢弃本次结果
            if (Directory.Exists(target))
            {
                TryDeleteDirectory(staging);
                return;
            }
            try
            {
                Directory.Move(staging, target);
            }
            catch (IOException)
            {
                // 并发下别人先移好了，或跨卷：退化为逐文件复制
                if (IsComplete(target)) { TryDeleteDirectory(staging); return; }
                CopyDirectory(staging, target);
                TryDeleteDirectory(staging);
            }
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
    }

    /// <summary>逐文件复制（跨卷改名失败时的兜底）。</summary>
    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        }
    }

    /// <summary>后台清理同级的旧载荷目录（升级 FFmpeg 后的残留，约 34 MB）。</summary>
    private static void CleanupOldPayloads(string current)
    {
        var root = Path.GetDirectoryName(current);
        if (string.IsNullOrEmpty(root)) return;
        Task.Run(() =>
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(root))
                {
                    if (string.Equals(dir, current, StringComparison.OrdinalIgnoreCase)) continue;
                    // 只清理看起来是载荷目录的项，避开正在解压的临时目录
                    var name = Path.GetFileName(dir);
                    if (name.StartsWith(".tmp-", StringComparison.Ordinal)) continue;
                    var marker = Path.Combine(dir, MarkerFileName);
                    if (!File.Exists(marker)) continue;
                    // 给别的实例留缓冲：5 分钟内动过的不碰
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) < TimeSpan.FromMinutes(5)) continue;
                    TryDeleteDirectory(dir);
                }
            }
            catch
            {
                // 清理失败不影响功能
            }
        });
    }

    /// <summary>尽力删除目录，失败静默（可能被占用）。</summary>
    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>写一条载荷相关日志；日志失败不影响功能。</summary>
    private static void LogPayload(string message)
    {
        try { Log.Info($"[载荷] {message}"); } catch { /* 忽略 */ }
    }
}
