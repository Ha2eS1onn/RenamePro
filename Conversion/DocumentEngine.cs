using System.Diagnostics;
using Microsoft.Win32;
using RenamePro.Core;

namespace RenamePro.Conversion;

/// <summary>文档引擎类型。</summary>
public enum DocumentEngineKind
{
    /// <summary>没有任何可用引擎。</summary>
    None,

    /// <summary>系统安装的 LibreOffice（无头子进程）。</summary>
    LibreOfficeSystem,

    /// <summary>随包目录里的 LibreOffice（全功能版自带的 LibreOffice\，与系统安装用法完全相同）。</summary>
    LibreOfficeBundled,

    /// <summary>Microsoft Office COM 自动化（Word / PowerPoint / Excel）。</summary>
    OfficeCom,

    /// <summary>内置 Markdown → docx 转换器（不依赖外部程序）。</summary>
    BuiltInMarkdown
}

/// <summary>引擎探测结果。</summary>
/// <param name="Kind">引擎类型</param>
/// <param name="Detail">引擎标识（路径或版本），写日志用</param>
/// <param name="Error">不可用原因（可用时为 null）</param>
public sealed record DocumentEngineStatus(DocumentEngineKind Kind, string? Detail, string? Error)
{
    /// <summary>是否可用。</summary>
    public bool IsAvailable => Kind != DocumentEngineKind.None;
}

/// <summary>
/// 文档引擎探测：按配置把"重活"交给哪一个外部程序。
///
/// 优先级（documentEngine=auto）：环境变量覆盖 → 随包目录 → 系统 LibreOffice → Office COM。
///   随包目录（全功能版自带的 LibreOffice\）排在最前：它是我们固定版本、随发布做过转换冒烟的那一份，
///   而用户机器上的系统安装可能是多年前的版本；想用系统那份就把 documentEngine 设成 "libreoffice"。
///   系统 LibreOffice 作为没随包分发时的首选：命令行契约固定、无头模式不会弹窗、
///   不占用用户已打开的 Word/Excel 实例，也不需要 STA 线程与 COM 封送。
///   Office COM 放在最后：它要求机器装了 Office、要求 STA 且会被"文件已在 Word 里打开"直接挡住，
///   但在只有 Office 的机器上是唯一的选项，因此保留为可选路径。
///
/// 候选是否可用以 `soffice --version` 自检为准（缺 DLL / 被杀软拦截 / 目录被复制了一半都在这里暴露），
/// 绝不在探测路径上去遍历几千条引擎文件。
///
/// 探测结果缓存在进程内（重新加载配置时调用 <see cref="InvalidateCache"/> 失效）。
/// COM 探测只读注册表（Type.GetTypeFromProgID），绝不为了探测而启动 Word/Excel。
/// </summary>
public static class DocumentEngine
{
    /// <summary>探测结果缓存；null 表示尚未探测。</summary>
    private static readonly object Sync = new();
    private static DocumentEngineStatus? _cached;
    private static bool _comWord;
    private static bool _comPowerPoint;
    private static bool _comExcel;

    /// <summary>环境变量覆盖：显式指定 soffice(.com/.exe) 路径（便于测试与便携部署）。</summary>
    private const string SofficeOverrideVariable = "RENAMEPRO_SOFFICE";

    /// <summary>随包引擎目录名（全功能版的发布结构：&lt;程序目录&gt;\LibreOffice\program\soffice.com）。</summary>
    private const string BundledDirectoryName = "LibreOffice";

    /// <summary>随包引擎目录的覆盖变量：把引擎放到别处（便携部署/测试）时显式指定目录。</summary>
    private const string BundledDirVariable = "RENAMEPRO_DOCENGINE";

    /// <summary>解析到的 LibreOffice 可执行文件（soffice.com 优先，拿得到退出码）。</summary>
    private static string? _sofficePath;

    /// <summary>上次探测时解析出的 LibreOffice 版本号（仅用于日志）。</summary>
    private static string? _sofficeVersion;

    /// <summary>LibreOffice 是否已解析到（供 DocConverter 直接取用）。</summary>
    public static string? SofficePath
    {
        get { lock (Sync) return _sofficePath; }
    }

    /// <summary>LibreOffice 版本号（探测时解析，可能为 null）。</summary>
    public static string? SofficeVersion
    {
        get { lock (Sync) return _sofficeVersion; }
    }

    /// <summary>配置重新加载后清空探测缓存。</summary>
    public static void InvalidateCache()
    {
        lock (Sync)
        {
            _cached = null;
            _sofficePath = null;
            _sofficeVersion = null;
        }
    }

    /// <summary>
    /// 探测可用引擎（按配置的优先级顺序），结果缓存。
    /// </summary>
    /// <returns>引擎状态</returns>
    public static DocumentEngineStatus Probe()
    {
        lock (Sync)
        {
            return _cached ??= Detect();
        }
    }

    /// <summary>按配置顺序探测：auto / libreoffice / bundled / com。</summary>
    private static DocumentEngineStatus Detect()
    {
        var mode = AppConfig.Current.DocumentEngine;
        var failures = new List<string>();

        // ① 环境变量覆盖（测试与便携部署用）：显式给了就用它，不再猜
        if (mode != "com")
        {
            var overridden = TryEnvironmentOverride(failures);
            if (overridden != null) return overridden;
        }

        // ② 随包目录（全功能版自带，优先级最高）
        if (mode is "auto" or "bundled")
        {
            var bundled = TryBundledLibreOffice(failures);
            if (bundled != null) return bundled;
            if (mode == "bundled")
            {
                return new DocumentEngineStatus(DocumentEngineKind.None, null,
                    string.Join("；", failures));
            }
        }

        // ③ 系统安装（documentEngine=libreoffice 时只看这条）
        if (mode is "auto" or "libreoffice")
        {
            var system = TrySystemLibreOffice(failures);
            if (system != null) return system;
            if (mode == "libreoffice")
            {
                return new DocumentEngineStatus(DocumentEngineKind.None, null,
                    string.Join("；", failures));
            }
        }

        // ④ Office COM（唯一需要 STA 与已安装 Office 的路径，放最后）
        if (mode is "auto" or "com")
        {
            var com = TryOfficeCom(failures);
            if (com != null) return com;
        }

        if (failures.Count == 0) failures.Add("未找到任何可用的文档引擎");
        return new DocumentEngineStatus(DocumentEngineKind.None, null, string.Join("；", failures));
    }

    /// <summary>
    /// 环境变量 <c>RENAMEPRO_SOFFICE</c> 显式指定的引擎（测试与便携部署用）。
    /// 变量没设或指向的文件不存在时返回 null，并把原因记进 <paramref name="failures"/> 后继续走后面的候选。
    /// </summary>
    /// <param name="failures">原因累积列表</param>
    /// <returns>可用时返回状态，否则 null</returns>
    private static DocumentEngineStatus? TryEnvironmentOverride(List<string> failures)
    {
        var overridePath = Environment.GetEnvironmentVariable(SofficeOverrideVariable);
        if (string.IsNullOrWhiteSpace(overridePath)) return null;
        if (!File.Exists(overridePath))
        {
            failures.Add($"环境变量 {SofficeOverrideVariable} 指向的文件不存在：{overridePath}");
            return null;
        }
        return Accept(overridePath, $"环境变量 {SofficeOverrideVariable}",
            DocumentEngineKind.LibreOfficeSystem, failures);
    }

    /// <summary>
    /// 随包目录里的 LibreOffice（<c>&lt;程序目录&gt;\LibreOffice\program\soffice.com</c>）。
    /// 目录不存在或自检失败都只记原因，不影响后面的系统安装/COM 候选。
    /// </summary>
    /// <param name="failures">原因累积列表</param>
    /// <returns>可用时返回状态，否则 null</returns>
    private static DocumentEngineStatus? TryBundledLibreOffice(List<string> failures)
    {
        foreach (var path in EnumerateBundledSofficePaths())
        {
            if (!File.Exists(path)) continue;
            var accepted = Accept(path, "随包目录", DocumentEngineKind.LibreOfficeBundled, failures);
            if (accepted != null) return accepted;
        }
        failures.Add($"未找到随包目录里的 LibreOffice（{BundledDirectory}\\program\\soffice.com）");
        return null;
    }

    /// <summary>系统安装的 LibreOffice（常见安装目录 + 注册表 + PATH）。</summary>
    /// <param name="failures">原因累积列表</param>
    /// <returns>可用时返回状态，否则 null</returns>
    private static DocumentEngineStatus? TrySystemLibreOffice(List<string> failures)
    {
        foreach (var path in EnumerateSystemSofficePaths())
        {
            if (!File.Exists(path)) continue;
            var accepted = Accept(path, "系统安装", DocumentEngineKind.LibreOfficeSystem, failures);
            if (accepted != null) return accepted;
        }
        failures.Add("未找到 LibreOffice（已探测常见安装目录、注册表与 PATH）");
        return null;
    }

    /// <summary>
    /// 接受一个候选：跑一次 <c>--version</c>，能启动才算可用
    /// （缺 DLL / 被杀软拦截 / 目录只复制了一半都在这里暴露，而不是等到第一次转换）。
    /// </summary>
    /// <param name="path">soffice 可执行文件路径</param>
    /// <param name="origin">来源描述（写日志用）</param>
    /// <param name="kind">引擎类型</param>
    /// <param name="failures">原因累积列表</param>
    /// <returns>可用时返回状态，否则 null</returns>
    private static DocumentEngineStatus? Accept(string path, string origin, DocumentEngineKind kind,
        List<string> failures)
    {
        var version = RunVersion(path, out var versionError);
        if (version == null)
        {
            failures.Add($"LibreOffice 自检失败（{path}）：{versionError}");
            return null;
        }

        _sofficePath = path;
        _sofficeVersion = version;
        return new DocumentEngineStatus(kind, $"{version}（{path}，{origin}）", null);
    }

    /// <summary>
    /// 随包引擎目录：默认 <c>&lt;程序目录&gt;\LibreOffice</c>，可用 <c>RENAMEPRO_DOCENGINE</c> 指向别处。
    /// 只做字符串拼装（不做存在性判断），因为托盘菜单会调它来显示状态，不能引入任何 IO。
    /// </summary>
    public static string BundledDirectory
    {
        get
        {
            var overrideDir = Environment.GetEnvironmentVariable(BundledDirVariable);
            if (!string.IsNullOrWhiteSpace(overrideDir))
            {
                try { return Path.GetFullPath(overrideDir); }
                catch { /* 路径非法时退回默认目录 */ }
            }
            return Path.Combine(AppContext.BaseDirectory, BundledDirectoryName);
        }
    }

    /// <summary>
    /// 随包目录里的引擎可执行文件（找不到返回 null）。探测器与自检共用，避免两处各写一套路径规则。
    /// </summary>
    public static string? FindBundledSoffice() => EnumerateBundledSofficePaths().FirstOrDefault(File.Exists);

    /// <summary>
    /// 枚举随包目录里的 soffice 候选。soffice.com 优先：它是控制台宿主，会等转换真正结束才返回并给出退出码；
    /// soffice.exe 会立刻分离返回（拿不到真实结果），只在 .com 缺失时兜底。
    /// </summary>
    private static IEnumerable<string> EnumerateBundledSofficePaths()
    {
        var directory = BundledDirectory;
        yield return Path.Combine(directory, "program", "soffice.com");
        yield return Path.Combine(directory, "program", "soffice.exe");
    }

    /// <summary>
    /// 枚举系统中可能的 soffice 路径。soffice.com 优先于 soffice.exe：
    /// .com 是控制台宿主，会等转换真正结束才返回并给出退出码；.exe 会立刻分离返回（拿不到真实结果）。
    /// </summary>
    private static IEnumerable<string> EnumerateSystemSofficePaths()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetEnvironmentVariable("ProgramW6432"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            yield return Path.Combine(root, "LibreOffice", "program", "soffice.com");
            yield return Path.Combine(root, "LibreOffice", "program", "soffice.exe");
            yield return Path.Combine(root, "Programs", "LibreOffice", "program", "soffice.com");
            yield return Path.Combine(root, "Programs", "LibreOffice", "program", "soffice.exe");
        }

        // 注册表：App Paths 与 LibreOffice 自己的 UNO 安装路径（HKLM/HKCU 都看）
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                foreach (var subKey in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\soffice.exe", @"SOFTWARE\LibreOffice\UNO\InstallPath" })
                {
                    var value = ReadRegistry(hive, view, subKey);
                    if (string.IsNullOrEmpty(value)) continue;
                    if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(value))
                    {
                        yield return value;
                        // .exe 旁边通常有 .com，优先用它
                        var comPath = Path.ChangeExtension(value, ".com");
                        if (File.Exists(comPath)) yield return comPath;
                    }
                    else
                    {
                        yield return Path.Combine(value, "soffice.com");
                        yield return Path.Combine(value, "soffice.exe");
                    }
                }
            }
        }
    }

    /// <summary>读注册表字符串值（不存在/无权访问时返回 null）。</summary>
    private static string? ReadRegistry(RegistryHive hive, RegistryView view, string subKey)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKey);
            return key?.GetValue(null) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>跑 `soffice --version` 并解析版本号；失败返回 null 并给出原因。</summary>
    /// <param name="sofficePath">soffice 可执行文件路径</param>
    /// <param name="error">失败原因</param>
    private static string? RunVersion(string sofficePath, out string? error)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = sofficePath,
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                error = "进程无法启动";
                return null;
            }
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(20_000))
            {
                TryKill(process);
                error = "--version 20 秒未返回";
                return null;
            }
            if (process.ExitCode != 0)
            {
                error = $"退出码 {process.ExitCode}：{stderr.Trim()}";
                return null;
            }
            var text = (stdout + " " + stderr).Trim();
            // 形如 "LibreOffice 24.8.4.2 420(Build:2)"；只取 "LibreOffice x.y..." 前缀
            var index = text.IndexOf("LibreOffice", StringComparison.OrdinalIgnoreCase);
            var version = index >= 0 ? text[index..].Split('\n')[0].Trim() : text;
            if (version.Length == 0)
            {
                error = "版本输出为空";
                return null;
            }
            error = null;
            return version.Length > 80 ? version[..80] : version;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// 探测 Office COM：只读注册表判断 ProgID 是否注册，绝不启动 Office。
    /// 注意这是"按文档族"的能力：只装了 Excel 的机器只能处理表格。
    /// </summary>
    /// <param name="failures">原因累积列表</param>
    private static DocumentEngineStatus? TryOfficeCom(List<string> failures)
    {
        _comWord = IsProgIdRegistered("Word.Application");
        _comPowerPoint = IsProgIdRegistered("PowerPoint.Application");
        _comExcel = IsProgIdRegistered("Excel.Application");

        if (!_comWord && !_comPowerPoint && !_comExcel)
        {
            failures.Add("Office COM 不可用（未注册 Word.Application / PowerPoint.Application / Excel.Application）");
            return null;
        }

        var families = new List<string>();
        if (_comWord) families.Add("Word");
        if (_comPowerPoint) families.Add("PowerPoint");
        if (_comExcel) families.Add("Excel");
        return new DocumentEngineStatus(DocumentEngineKind.OfficeCom,
            $"Microsoft Office COM（{string.Join(" / ", families)}）", null);
    }

    /// <summary>ProgID 是否已注册（等价于对应 Office 应用已安装）。</summary>
    /// <param name="progId">ProgID，如 Word.Application</param>
    public static bool IsProgIdRegistered(string progId)
    {
        try
        {
            return Type.GetTypeFromProgID(progId) != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>COM 路径下当前可用的 Office 应用（探测结果缓存）。</summary>
    public static (bool Word, bool PowerPoint, bool Excel) ComAvailability
    {
        get
        {
            Probe();
            lock (Sync) return (_comWord, _comPowerPoint, _comExcel);
        }
    }

    /// <summary>引擎不可用的警告块是否已经写过（启动自检与首个任务共用，保证只写一次）。</summary>
    private static int _unavailableReported;

    /// <summary>
    /// 写"文档转换已禁用"的警告块，进程内只写一次。
    ///
    /// 为什么要去重：启动自检（docWarmupOnStart）与第一个文档任务都会发现引擎不可用，
    /// 两处各写一次就会在 log.txt 里出现两段一模一样的警告，与 README 的"一段警告"描述不符，
    /// 也让用户误以为出了两次问题。
    /// </summary>
    /// <param name="error">不可用原因</param>
    public static void ReportUnavailableOnce(string? error)
    {
        if (Interlocked.CompareExchange(ref _unavailableReported, 1, 0) != 0) return;
        Log.Warn($"文档转换已禁用（图片/音视频功能不受影响）：{error}");
        Log.Warn("  常见原因：本机没装 LibreOffice，且程序目录里没有随包引擎目录 (LibreOffice\\program\\soffice.com)、" +
                 "把 RenamePro.exe 单独复制了出来、Office COM 不可用、或杀毒软件拦截了引擎文件；" +
                 "详见 README 的「文档转换引擎」一节");
    }

    /// <summary>
    /// 不阻塞启动的引擎自检：探测 + 一次极小的预热转换。
    /// 预热的目的是把 LibreOffice 首次创建用户配置目录的几秒代价挪到启动后台，
    /// 而不是让用户的第一次转换去承担。任何失败都只写日志，不影响托盘与其他功能。
    /// </summary>
    public static async Task WarmUpAsync()
    {
        try
        {
            var status = Probe();
            if (!status.IsAvailable)
            {
                ReportUnavailableOnce(status.Error);
                return;
            }

            Log.Info($"[文档] 引擎就绪：{status.Detail}");
            if (status.Kind is DocumentEngineKind.LibreOfficeSystem or DocumentEngineKind.LibreOfficeBundled)
            {
                var watch = Stopwatch.StartNew();
                var ok = await DocConverter.WarmUpLibreOfficeAsync().ConfigureAwait(false);
                watch.Stop();
                Log.Info(ok
                    ? $"[文档] 预热完成（{watch.ElapsedMilliseconds} ms，首次会创建 LibreOffice 用户配置目录）"
                    : $"[文档] 预热未成功（{watch.ElapsedMilliseconds} ms），首次转换可能较慢；不影响功能");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[文档] 引擎自检异常：{ex.Message}");
        }
    }

    /// <summary>杀掉子进程（尽力而为，失败静默）。</summary>
    /// <param name="process">目标进程</param>
    internal static void TryKill(Process process)
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
