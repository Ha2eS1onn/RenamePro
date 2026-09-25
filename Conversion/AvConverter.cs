using System.Diagnostics;
using System.Text.Json;
using RenamePro.Core;

namespace RenamePro.Conversion;

/// <summary>ffprobe 探测结果：流编码列表、总时长、是否含视频流。</summary>
public sealed class AvProbeResult
{
    /// <summary>全部流的（类型, 编码）列表，类型如 video / audio / subtitle。</summary>
    public List<(string Type, string Codec)> Streams { get; } = new();

    /// <summary>总时长（秒）；0 表示未知（无总时长时进度未知，里程碑 3 显示滚动条）。</summary>
    public double DurationSeconds { get; set; }

    /// <summary>是否包含视频流。</summary>
    public bool HasVideo => Streams.Exists(s => s.Type == "video");

    /// <summary>音频流编码序列。</summary>
    public IEnumerable<string> AudioCodecs => Streams.Where(s => s.Type == "audio").Select(s => s.Codec);

    /// <summary>视频流编码序列。</summary>
    public IEnumerable<string> VideoCodecs => Streams.Where(s => s.Type == "video").Select(s => s.Codec);
}

/// <summary>音视频执行计划类型。</summary>
public enum AvPlanKind
{
    /// <summary>流拷贝（-c copy 仅重封装，秒级，与文件大小几乎无关）。</summary>
    StreamCopy,

    /// <summary>视频重编码（libx264 等，进音视频队列串行执行）。</summary>
    VideoReencode,

    /// <summary>音频编码（纯音频互转 / 提取音轨 / 音频流封装）。</summary>
    AudioEncode
}

/// <summary>音视频执行计划：决策结果与说明（用于日志）。</summary>
public sealed record AvPlan(AvPlanKind Kind, string Description);

/// <summary>
/// 音视频转换器：ffprobe 探测 + ffmpeg 子进程执行。
/// 流拷贝优先（-c copy 仅重封装，秒级）；流不兼容或编码不在白名单才重编码；
/// 进度经 -progress pipe:1 解析 out_time_ms/换算百分比，stderr 全量捕获记错误。
/// ffmpeg.exe 缺失时仅禁用音视频功能，图片功能不受影响。
/// </summary>
public static class AvConverter
{
    /// <summary>规格给定的流拷贝编码白名单。</summary>
    private static readonly HashSet<string> StreamCopyWhitelist = new(StringComparer.Ordinal)
    {
        "h264", "hevc", "aac", "ac3", "opus", "vorbis", "mp3"
    };

    /// <summary>有损音频编码（用于“有损 → 无损”提示）。</summary>
    private static readonly HashSet<string> LossyAudioCodecs = new(StringComparer.Ordinal)
    {
        "mp3", "aac", "vorbis", "opus", "ac3", "wmav2", "wmapro"
    };

    /// <summary>纯音频扩展名。</summary>
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.Ordinal)
    {
        ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a"
    };

    /// <summary>视频容器扩展名。</summary>
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.Ordinal)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".ts", ".webm", ".flv"
    };

    /// <summary>各容器流拷贝时可接受的编码（“流不兼容”判定依据）。</summary>
    private static readonly Dictionary<string, HashSet<string>> ContainerCodecs = new(StringComparer.Ordinal)
    {
        [".mp4"] = new(StringComparer.Ordinal) { "h264", "hevc", "aac", "ac3", "mp3" },
        [".mov"] = new(StringComparer.Ordinal) { "h264", "hevc", "aac", "ac3", "mp3" },
        [".m4a"] = new(StringComparer.Ordinal) { "aac", "mp3", "alac" },
        [".mkv"] = new(StringComparer.Ordinal) { "h264", "hevc", "aac", "ac3", "opus", "vorbis", "mp3", "flac", "vp8", "vp9", "av1", "pcm_s16le" },
        [".webm"] = new(StringComparer.Ordinal) { "vp8", "vp9", "av1", "opus", "vorbis" },
        [".ts"] = new(StringComparer.Ordinal) { "h264", "hevc", "aac", "ac3", "mp3" },
        [".flv"] = new(StringComparer.Ordinal) { "h264", "aac", "mp3" },
        [".avi"] = new(StringComparer.Ordinal) { "h264", "mpeg4", "mp3", "ac3", "aac" },
        [".wmv"] = new(StringComparer.Ordinal) { "wmv3", "vc1", "wmav2", "wmapro" }
    };

    /// <summary>ffmpeg.exe 完整路径（程序同目录）。</summary>
    public static string FfmpegPath => Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");

    /// <summary>ffprobe.exe 完整路径（程序同目录）。</summary>
    public static string FfprobePath => Path.Combine(AppContext.BaseDirectory, "ffprobe.exe");

    /// <summary>ffmpeg / ffprobe 是否可用。</summary>
    public static bool IsAvailable => File.Exists(FfmpegPath) && File.Exists(FfprobePath);

    /// <summary>
    /// 用 ffprobe 探测流编码与总时长。
    /// </summary>
    /// <param name="path">媒体文件完整路径（兼容中文与空格，参数直接加引号，不做 URI 转换）</param>
    /// <returns>探测结果</returns>
    public static AvProbeResult Probe(string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FfprobePath,
            // 只取必要字段，-of json 便于稳健解析
            Arguments = $"-v error -show_entries stream=codec_type,codec_name -show_entries format=duration -of json {Quote(path)}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("ffprobe 启动失败");
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffprobe 退出码 {process.ExitCode}：{stderrTask.Result.Trim()}");

        var result = new AvProbeResult();
        using var doc = JsonDocument.Parse(stdout);
        if (doc.RootElement.TryGetProperty("streams", out var streams))
        {
            foreach (var item in streams.EnumerateArray())
            {
                var type = item.TryGetProperty("codec_type", out var t) ? t.GetString() ?? "" : "";
                var codec = item.TryGetProperty("codec_name", out var c) ? c.GetString() ?? "" : "";
                result.Streams.Add((type, codec));
            }
        }
        if (doc.RootElement.TryGetProperty("format", out var format)
            && format.TryGetProperty("duration", out var duration)
            && double.TryParse(duration.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds))
        {
            result.DurationSeconds = seconds;
        }
        return result;
    }

    /// <summary>
    /// 根据源/目标扩展名与探测结果生成执行计划（流拷贝优先）；
    /// 不在支持矩阵内的组合返回 null（静默忽略）。
    /// </summary>
    /// <param name="task">任务模型</param>
    /// <param name="probe">ffprobe 探测结果</param>
    /// <returns>执行计划或 null</returns>
    public static AvPlan? BuildPlan(ConversionTask task, AvProbeResult probe)
    {
        // 目标为音频容器：提取 / 重编码音轨（视频文件即提取音轨，音频文件互转同命令）
        if (AudioExtensions.Contains(task.NewExt))
        {
            // 有损 → 无损不阻断，但记提示
            if (task.NewExt is ".flac" or ".wav" && probe.AudioCodecs.Any(c => LossyAudioCodecs.Contains(c)))
                Log.Info("提示：转码为无损不会提升音质");
            return new AvPlan(AvPlanKind.AudioEncode, $"音频编码（目标 {task.NewExt}）");
        }

        // 目标为视频容器
        if (!VideoExtensions.Contains(task.NewExt)) return null; // 不在支持矩阵内

        if (probe.HasVideo)
        {
            // 视频 → 视频：编码全在拷贝白名单且目标容器接受 → 流拷贝（秒级）；否则重编码
            var all = probe.Streams.Where(s => s.Type is "video" or "audio").Select(s => s.Codec).ToList();
            if (all.Count > 0 && all.All(c => StreamCopyWhitelist.Contains(c) && CanStreamCopy(task.NewExt, c)))
                return new AvPlan(AvPlanKind.StreamCopy, "流拷贝（仅重封装，秒级）");
            return new AvPlan(AvPlanKind.VideoReencode, "视频重编码");
        }

        // 音频 → 视频：仅音频流封装，可 copy 则 copy
        var audio = probe.AudioCodecs.ToList();
        if (audio.Count > 0 && audio.All(c => CanStreamCopy(task.NewExt, c)))
            return new AvPlan(AvPlanKind.StreamCopy, "音频流封装（流拷贝）");
        return new AvPlan(AvPlanKind.AudioEncode, $"音频流封装（目标 {task.NewExt}）");
    }

    /// <summary>
    /// 执行 ffmpeg 转换：写入 targetPath（临时文件）；取消时杀掉整个进程树。
    /// </summary>
    /// <param name="task">任务模型（提供进度上报）</param>
    /// <param name="plan">执行计划</param>
    /// <param name="probe">探测结果（换算进度用）</param>
    /// <param name="sourcePath">源文件（改名后文件，内容为旧格式）</param>
    /// <param name="targetPath">目标临时文件</param>
    /// <param name="token">取消令牌</param>
    public static async Task ExecuteAsync(ConversionTask task, AvPlan plan, AvProbeResult probe,
        string sourcePath, string targetPath, CancellationToken token)
    {
        var args = BuildArgs(plan, sourcePath, targetPath, task.NewExt);
        Log.Info($"[ffmpeg] {plan.Description} | {args}");

        var startInfo = new ProcessStartInfo
        {
            FileName = FfmpegPath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // 取消时终止整个 ffmpeg 进程树
        using var registration = token.Register(() => TryKill(process));

        string stderr = "";
        try
        {
            var stderrTask = process.StandardError.ReadToEndAsync();
            // -progress pipe:1 输出 key=value 行；out_time_us 为已处理时长（微秒）
            while (await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) is { } line)
            {
                var index = line.IndexOf('=');
                if (index <= 0) continue;
                if (line.Substring(0, index) == "out_time_us"
                    && long.TryParse(line.Substring(index + 1), out var microseconds))
                {
                    ReportProgress(task, probe, microseconds);
                }
            }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            stderr = await stderrTask.ConfigureAwait(false);
        }
        finally
        {
            if (!process.HasExited) TryKill(process);
        }

        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg 退出码 {process.ExitCode}：{stderr.Trim()}");
    }

    /// <summary>按总时长换算进度百分比；无总时长上报 -1（进度未知）。</summary>
    private static void ReportProgress(ConversionTask task, AvProbeResult probe, long microseconds)
    {
        if (task.Progress == null) return;
        if (probe.DurationSeconds <= 0)
        {
            task.Progress.Report(-1); // 进度未知（里程碑 3 显示滚动条）
            return;
        }
        var percent = (int)Math.Clamp(microseconds / (probe.DurationSeconds * 1_000_000.0) * 100, 0, 100);
        task.Progress.Report(percent);
    }

    /// <summary>拼接 ffmpeg 参数（路径加引号，兼容中文与空格，不做 URI 转换）。</summary>
    private static string BuildArgs(AvPlan plan, string sourcePath, string targetPath, string targetExt)
    {
        var src = Quote(sourcePath);
        var dst = Quote(targetPath);
        return plan.Kind switch
        {
            // 规格命令：流拷贝只重封装
            AvPlanKind.StreamCopy => $"-progress pipe:1 -i {src} -c copy -y {dst}",
            AvPlanKind.VideoReencode => $"-progress pipe:1 -i {src} {VideoEncodeArgs(targetExt)} -y {dst}",
            _ => $"-progress pipe:1 -i {src} -vn {AudioEncodeArgs(targetExt)} -y {dst}"
        };
    }

    /// <summary>视频重编码参数：规格默认 libx264/aac；WebM/WMV 容器用其专属编码。</summary>
    private static string VideoEncodeArgs(string targetExt) => targetExt switch
    {
        // WebM 容器只接受 VP8/VP9/AV1 + Opus/Vorbis
        ".webm" => "-c:v libvpx-vp9 -crf 32 -b:v 0 -c:a libopus",
        // WMV(ASF) 容器使用微软自家编码
        ".wmv" => "-c:v wmv2 -b:v 2M -c:a wmav2 -b:a 128k",
        // 其余容器按规格默认参数
        _ => "-c:v libx264 -crf 23 -preset veryfast -c:a aac"
    };

    /// <summary>音频编码参数：目标 mp3 用 libmp3lame，aac/m4a 用 aac，其余按容器选择。</summary>
    private static string AudioEncodeArgs(string targetExt) => targetExt switch
    {
        ".mp3" => "-c:a libmp3lame -q:a 2",
        ".aac" or ".m4a" => "-c:a aac -b:a 128k",
        ".ogg" => "-c:a libvorbis -q:a 5",
        ".flac" => "-c:a flac",
        ".wav" => "-c:a pcm_s16le",
        ".webm" => "-c:a libopus -b:a 128k",
        ".wmv" => "-c:a wmav2 -b:a 128k",
        _ => "-c:a aac -b:a 128k" // 音频流封装进 mp4/mov/mkv/ts/avi/flv
    };

    /// <summary>判断某编码能否流拷贝进目标容器。</summary>
    private static bool CanStreamCopy(string targetExt, string codec) =>
        ContainerCodecs.TryGetValue(targetExt, out var accepted) && accepted.Contains(codec);

    /// <summary>给参数加双引号（Windows 文件名不允许包含引号，无须转义）。</summary>
    private static string Quote(string path) => "\"" + path + "\"";

    /// <summary>杀掉 ffmpeg 进程树（取消时调用），失败静默忽略。</summary>
    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 进程可能刚好退出
        }
    }
}