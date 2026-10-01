using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RenamePro.Core;

namespace RenamePro.Conversion;

/// <summary>
/// 文档转换执行器：把一次文档转换在"引擎工作目录"里跑完，并把产物落到主流程给的临时路径。
///
/// 关键设计（外置引擎的产物命名规则与我们的临时文件名不一致）：
///   LibreOffice 的 --convert-to 会把输出命名为"源文件名 + 目标扩展名"，而不是我们想要的
///   Guid 临时文件名。因此这里先把源文件按"原始文件名 + Guid"暂存进一个私有工作目录
///   （工作目录建在目标文件同目录，保证最后 File.Move 是同卷操作），转换完成后按
///   ① 预期文件名 → ② 工作目录里新增的目标扩展名文件 的顺序找回产物，再 Move 到 targetPath。
///   主流程只看到 targetPath，完全不需要知道引擎的命名规则。
///
/// 成功判定以"产物存在且非空"为准，绝不只看退出码：LibreOffice 在失败时也经常返回 0。
///
/// 取消/超时：kill 整个进程树（soffice.exe 只是启动器，只杀它会留下持有配置锁的 soffice.bin），
/// 并清理工作目录。
/// </summary>
public static class DocConverter
{
    /// <summary>转换超时的额外放宽：每 MB 源文件加 3 秒（大文件给足时间，卡死仍能按时收场）。</summary>
    private const int ExtraSecondsPerMegabyte = 3;

    /// <summary>超时上限（秒）：无论文件多大都不超过 30 分钟。</summary>
    private const int MaxTimeoutSeconds = 1800;

    /// <summary>预热超时（秒）：首次创建 LibreOffice 用户配置目录需要额外时间。</summary>
    private const int WarmUpTimeoutSeconds = 120;

    /// <summary>工作目录名前缀（用于残留清理与识别）。</summary>
    private const string WorkDirectoryPrefix = ".renamepro-doc-";

    /// <summary>已创建过的 LibreOffice profile 槽位数量（惰性增长，最多 maxConcurrency 个）。</summary>
    private static readonly object ProfileSync = new();
    private static readonly SemaphoreSlim ProfileSlots = new(1, 1);
    private static int _profileSlotCount;

    /// <summary>已经成功产出过产物的 profile 槽位（优先复用，避开"全新 profile 首次不产出"的坑）。</summary>
    private static readonly List<int> _provenSlots = new();

    /// <summary>
    /// profile 根目录（首次使用时确定并缓存）。
    /// 候选顺序：%RENAMEPRO_DOCROOT%（显式覆盖）→ %LOCALAPPDATA% → %TEMP% → 程序目录。
    /// 受限环境（权限收紧、漫游配置、沙箱）下 %LOCALAPPDATA% 可能不可写，
    /// 不准备退路就会出现"引擎装好了却用不了"，因此这里逐个实写探测。
    /// </summary>
    private static readonly Lazy<string> ProfileRootLazy = new(ResolveProfileRoot,
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>工作根目录的覆盖环境变量：便于测试把临时文件固定到可写位置，也方便用户自定义。</summary>
    private const string DocRootVariable = "RENAMEPRO_DOCROOT";

    /// <summary>探测并确定可写的 profile 根目录。</summary>
    private static string ResolveProfileRoot()
    {
        var candidates = new List<string>();
        var overrideRoot = Environment.GetEnvironmentVariable(DocRootVariable);
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            candidates.Add(Path.Combine(overrideRoot, "DocEngine"));
        }
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData)) candidates.Add(Path.Combine(localAppData, "RenamePro", "DocEngine"));
        candidates.Add(Path.Combine(Path.GetTempPath(), "RenamePro", "DocEngine"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "DocEngine"));

        var errors = new List<string>();
        foreach (var root in candidates)
        {
            try
            {
                Directory.CreateDirectory(root);
                // 只 CreateDirectory 不够：目录可能已存在但没有写权限（上一次以别的身份创建），
                // 因此再做一次真实写入探测。
                var probe = Path.Combine(root, ".write-probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                if (!string.Equals(root, candidates[0], StringComparison.OrdinalIgnoreCase))
                {
                    Log.Info($"[文档] 首选配置目录不可写，LibreOffice 用户配置改用：{root}");
                }
                return root;
            }
            catch (Exception ex)
            {
                errors.Add($"{root}（{ex.Message}）");
            }
        }
        throw new InvalidOperationException($"找不到可写的 LibreOffice 用户配置目录，已尝试：{string.Join("；", errors)}");
    }

    /// <summary>
    /// 预热用的工作根目录：同样尊重 %RENAMEPRO_DOCROOT%，否则用 %TEMP%\RenamePro。
    /// 失败时回退到程序目录，保证"临时目录不可写"的环境也能预热成功。
    /// </summary>
    private static string ResolveWarmUpRoot()
    {
        var candidates = new List<string>();
        var overrideRoot = Environment.GetEnvironmentVariable(DocRootVariable);
        if (!string.IsNullOrWhiteSpace(overrideRoot)) candidates.Add(Path.Combine(overrideRoot, "warmup"));
        candidates.Add(Path.Combine(Path.GetTempPath(), "RenamePro", "warmup"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "warmup"));

        foreach (var root in candidates)
        {
            try
            {
                Directory.CreateDirectory(root);
                var probe = Path.Combine(root, ".write-probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return root;
            }
            catch
            {
                // 试下一个
            }
        }
        return Path.Combine(AppContext.BaseDirectory, "warmup");
    }

    /// <summary>
    /// 执行文档转换。
    /// </summary>
    /// <param name="task">任务模型（提供扩展名、进度上报与源路径）</param>
    /// <param name="plan">转换计划（来自 <see cref="DocumentMatrix.Plan"/>）</param>
    /// <param name="targetPath">目标临时文件（Guid 命名 + 新扩展名）</param>
    /// <param name="token">取消令牌</param>
    public static async Task ExecuteAsync(ConversionTask task, DocumentPlan plan, string targetPath, CancellationToken token)
    {
        // 内置 Markdown 转换器：完全不落外置引擎，也不建工作目录
        if (plan.UseBuiltInMarkdown)
        {
            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                // 交给 File.ReadAllText 自动识别 BOM 与编码：UTF-8（含 BOM）、UTF-16、系统 ANSI 都能读，
                // 中文 Windows 上用记事本存出来的 GBK 文件不会变成乱码。
                var markdown = File.ReadAllText(task.NewPath);
                MarkdownToDocx.Convert(markdown, targetPath, Path.GetDirectoryName(task.NewPath)!);
                task.Progress?.Report(100);
            }, token).ConfigureAwait(false);
            return;
        }

        var workDirectory = CreateWorkDirectoryWithFallback(
            Path.GetDirectoryName(targetPath) ?? throw new InvalidOperationException($"目标路径没有父目录：{targetPath}"));
        string? stagedSource = null;
        var stagedAtUtc = DateTime.UtcNow;
        var releaseProfile = false;
        try
        {
            stagedSource = StageSource(task.NewPath, task.OldExt, workDirectory);
            stagedAtUtc = DateTime.UtcNow;

            switch (plan)
            {
                case { NeedsExternalEngine: false }:
                    break;
                default:
                    var status = DocumentEngine.Probe();
                    if (status.Kind == DocumentEngineKind.OfficeCom)
                    {
                        await RunOfficeComAsync(plan, stagedSource, targetPath, workDirectory, TaskExtOf(task), token)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await ProfileSlots.WaitAsync(token).ConfigureAwait(false);
                        releaseProfile = true;
                        var slot = AcquireProfileSlot();
                        var slotSucceeded = false;
                        try
                        {
                            await RunLibreOfficeAsync(plan, stagedSource, workDirectory, task, token).ConfigureAwait(false);
                            slotSucceeded = true;
                        }
                        finally
                        {
                            ReleaseProfileSlot(slot, slotSucceeded);
                        }
                    }
                    break;
            }

            token.ThrowIfCancellationRequested();

            // 找回产物并落位（LibreOffice 路径需要；COM 路径与内置转换器已经直接写到 targetPath）
            if (!File.Exists(targetPath))
            {
                var artifact = FindArtifact(workDirectory, task.NewExt, stagedSource, stagedAtUtc);
                if (artifact == null)
                {
                    throw new InvalidOperationException(
                        $"引擎没有产出目标文件（工作目录：{workDirectory}；内容：{DescribeDirectory(workDirectory)}）");
                }
                if (!string.Equals(Path.GetFileName(artifact), Path.GetFileName(targetPath), StringComparison.OrdinalIgnoreCase))
                {
                    Log.Info($"[文档] 产物已落位：{Path.GetFileName(artifact)} → {Path.GetFileName(targetPath)}");
                }
                IoRetry.Run("移动转换产物", () => File.Move(artifact, targetPath, overwrite: true));
            }

            var length = new FileInfo(targetPath).Length;
            if (length == 0)
            {
                throw new InvalidOperationException("引擎产出了 0 字节文件");
            }
            task.Progress?.Report(100);
            Log.Info($"[文档] 转换完成（{plan.Description}，产物 {length / 1024.0:0.0} KB）");
        }
        finally
        {
            if (releaseProfile) ProfileSlots.Release();
            TryDeleteDirectory(workDirectory);
        }
    }

    /// <summary>
    /// 自检用：对给定源文件真实跑一次转换，返回结果、耗时与错误。
    /// 与正式流程走**同一条代码路径**（同一个 DocConverter.ExecuteAsync），因此它能证明引擎通路是否正常，
    /// 也可以作为用户侧的排障工具（--docdiagnose）。
    /// </summary>
    /// <param name="sourcePath">源文件（内容与扩展名一致）</param>
    /// <param name="targetExt">目标扩展名（带点）</param>
    /// <returns>成功返回产物路径；失败返回 null 并给出原因（异步方法不能用 out 参数）</returns>
    internal static async Task<(string? OutputPath, string? Error)> DiagnoseAsync(string sourcePath, string targetExt)
    {
        var sourceExt = PathRules.NormalizeExt(Path.GetExtension(sourcePath));
        var plan = DocumentMatrix.Plan(sourceExt, targetExt, AppConfig.Current.AllowPdfSource, out var reason);
        if (plan == null) return (null, $"不在支持矩阵内：{reason}");
        if (!File.Exists(sourcePath)) return (null, "源文件不存在");

        // 只跑"引擎 → 产物"这一段：把源文件拷成"<原名>.diagrun<目标扩展名>"作为 newPath，
        // 调 ExecuteAsync 的引擎分支，产物落在同目录 Guid 临时文件里，最后改名成 <原名>.diag<扩展名>。
        // 主流程里的改名/备份/Atomic Replace 由 --selftest 与 verify-docengine.ps1 覆盖，
        // 这里只回答"这个引擎能不能把这个文件转成目标格式"——排障要的正是这个答案。
        var directory = Path.GetDirectoryName(sourcePath)!;
        var fileName = Path.GetFileNameWithoutExtension(sourcePath);
        var stagingPath = Path.Combine(directory, fileName + ".diagrun" + targetExt);
        if (File.Exists(stagingPath)) File.Delete(stagingPath);
        File.Copy(sourcePath, stagingPath);

        var tempTarget = Path.Combine(directory, Guid.NewGuid().ToString("N") + targetExt);
        // 注意 old/new 的语义：newPath 才是"要转换的文件"（内容为旧格式），扩展名也取自它。
        var task = new ConversionTask(sourcePath, stagingPath);
        var sourceLengthBefore = new FileInfo(sourcePath).Length;
        try
        {
            await ExecuteAsync(task, plan, tempTarget, CancellationToken.None).ConfigureAwait(false);
            if (!File.Exists(tempTarget) || new FileInfo(tempTarget).Length == 0)
            {
                return (null, "转换结束但没有产物");
            }
            // 诊断工具本身绝不能改动用户文件：源文件大小/时间被改过就说明实现有 bug
            var sourceAfter = new FileInfo(sourcePath);
            if (sourceAfter.Length != sourceLengthBefore)
            {
                return (null, $"诊断期间源文件被改动（{sourceLengthBefore} → {sourceAfter.Length} 字节），这是实现缺陷，请连同 log.txt 一起反馈");
            }
            var finalPath = Path.Combine(directory, fileName + ".diag" + targetExt);
            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(tempTarget, finalPath);
            Log.Info($"[文档] 诊断完成：{Path.GetFileName(sourcePath)} → {Path.GetFileName(finalPath)}（{new FileInfo(finalPath).Length} 字节）");
            return (finalPath, null);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tempTarget)) File.Delete(tempTarget); } catch { /* 忽略 */ }
            return (null, $"{ex.GetType().Name}：{ex.Message}");
        }
        finally
        {
            try { if (File.Exists(stagingPath)) File.Delete(stagingPath); } catch { /* 忽略 */ }
        }
    }

    /// <summary>诊断用：当前解析到的引擎与 profile 位置（便于排障时一眼看清环境）。</summary>
    internal static string DescribeEnvironment()
    {
        var status = DocumentEngine.Probe();
        var profileRoot = "(未解析)";
        try { profileRoot = ProfileRootLazy.Value; } catch (Exception ex) { profileRoot = $"(不可用：{ex.Message})"; }
        return $"引擎：{(status.IsAvailable ? status.Detail : "不可用（" + status.Error + "）")}{Environment.NewLine}" +
               $"配置目录：{profileRoot}{Environment.NewLine}" +
               $"工作根：{ResolveWarmUpRoot()}";
    }

    /// <summary>
    /// LibreOffice 无头转换：私有 profile + --convert-to + 产物回落。
    ///
    /// 关键经验（实测得出，改动前务必看）：**一个全新的 profile 目录第一次转换会失败**——
    /// LibreOffice 首次启动要初始化用户配置，在初始化未完成的这次运行里
    /// `--convert-to` 会直接结束（实测 exit=-1、不产出任何文件），而同一个 profile 的第二次就正常。
    /// 因此这里对"没产出产物"的情况自动重试一次（沿用同一个 profile，第二次即命中已初始化的配置），
    /// 并把这件事写进日志。用户侧表现为"第一次转换慢一点但成功"，而不是失败。
    /// </summary>
    /// <param name="plan">转换计划</param>
    /// <param name="stagedSource">暂存后的源文件</param>
    /// <param name="workDirectory">工作目录</param>
    /// <param name="task">任务模型</param>
    /// <param name="token">取消令牌</param>
    private static async Task RunLibreOfficeAsync(DocumentPlan plan, string stagedSource, string workDirectory,
        ConversionTask task, CancellationToken token)
    {
        var soffice = DocumentEngine.SofficePath
                      ?? throw new InvalidOperationException("LibreOffice 路径已失效，请重新加载配置后重试");

        var profileDirectory = EnsureProfileDirectory(AcquireProfileSlot());
        var profileUri = new Uri(profileDirectory).AbsoluteUri; // 中文用户名必须走 RFC3986 转义
        var timeout = EffectiveTimeoutSeconds(task.NewPath);

        // 参数全部加引号：路径可能含中文、空格与逗号
        var arguments =
            $"-env:UserInstallation={profileUri} " +
            "--headless --norestore --nolockcheck --nodefault --nofirststartwizard --nologo " +
            $"--convert-to {QuoteFilterSpec(plan)} --outdir {Quote(workDirectory)} {Quote(stagedSource)}";

        Log.Info($"[文档] {plan.Description} | 引擎: {DocumentEngine.SofficeVersion ?? "LibreOffice"} | 超时: {timeout}s");

        // 至多两次：第一次遇到"全新 profile 未初始化"时，第二次会成功
        var totalWatch = System.Diagnostics.Stopwatch.StartNew();
        for (var attempt = 1; ; attempt++)
        {
            var (exitCode, stderr, stdout) = await RunLibreOfficeOnceAsync(soffice, arguments, workDirectory, timeout, token)
                .ConfigureAwait(false);

            var produced = HasUsableArtifact(workDirectory, task.NewExt, stagedSource);
            if (exitCode == 0 && produced) return;

            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;

            // 只重试一次，且只在"没有产出产物"时重试；退出码非 0 且确有产物的情况不会走到这里。
            // 触发条件刻意包含"退出码为 0 但没产物"：LibreOffice 首次初始化用户配置时正是这种表现。
            if (attempt == 1 && !produced && totalWatch.Elapsed < TimeSpan.FromSeconds(timeout * 0.7))
            {
                Log.Info($"[文档] 首次未产出结果（退出码 {exitCode}，通常是 LibreOffice 首次初始化用户配置），" +
                         $"自动重试一次（{Trim(detail)}）");
                continue;
            }

            if (exitCode != 0)
            {
                throw new InvalidOperationException($"LibreOffice 退出码 {exitCode}：{Trim(detail)}");
            }
            // 退出码 0 但没产物：交给上层报"引擎没有产出目标文件"（附目录清单）
            return;
        }
    }

    /// <summary>跑一次 LibreOffice 进程；返回退出码与输出。超时/取消在这里抛出。</summary>
    /// <param name="soffice">soffice 可执行文件</param>
    /// <param name="arguments">命令行参数（已加引号）</param>
    /// <param name="workDirectory">工作目录</param>
    /// <param name="timeout">超时秒数</param>
    /// <param name="token">取消令牌</param>
    private static async Task<(int ExitCode, string Stderr, string Stdout)> RunLibreOfficeOnceAsync(
        string soffice, string arguments, string workDirectory, int timeout, CancellationToken token)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = soffice,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workDirectory
        };

        // 给引擎一个**确定可写**的临时目录：LibreOffice 导出 PDF/ODT 时会在自己的临时目录里
        // 落中间文件，如果 %TEMP% 不可写（受限账户、被策略重定向、或用户清理过目录），
        // 它会以"impl_store … failed: Error Area:Io Class:Write Code:16"结束且不产出任何文件——
        // 而 CSV/TXT 这类纯文本导出不走临时文件，于是表现为"有的格式能转、有的不能"，极难排查。
        // 我们发现并复现了这个问题，因此显式把它指向工作目录。
        var scratch = Path.Combine(workDirectory, "lo-tmp");
        Directory.CreateDirectory(scratch);
        startInfo.Environment["TMP"] = scratch;
        startInfo.Environment["TEMP"] = scratch;

        // 排障用：把真实命令行写进日志。LibreOffice 在参数有问题时只会静默不产出文件，
        // 没有这条日志就只能靠猜。
        Log.Info($"[文档] 调用：{Quote(soffice)} {arguments}");

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("LibreOffice 进程启动失败");

        // 取消：杀整个进程树（只杀启动器会留下持有 profile 锁的 soffice.bin）
        using var registration = token.Register(() => DocumentEngine.TryKill(process));

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var exitTask = process.WaitForExitAsync(CancellationToken.None);
        var cancelTask = Task.Delay(Timeout.Infinite, token);

        var finished = await Task.WhenAny(exitTask, cancelTask, Task.Delay(TimeSpan.FromSeconds(timeout), CancellationToken.None))
            .ConfigureAwait(false);

        if (finished != exitTask)
        {
            // 超时或取消：先杀树，再按原因抛
            DocumentEngine.TryKill(process);
            await WaitQuietlyAsync(exitTask, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException($"文档转换超时（{timeout} 秒）");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return (process.ExitCode, stderr, stdout);
    }

    /// <summary>工作目录里是否已经出现可用的目标格式产物。</summary>
    /// <param name="workDirectory">工作目录</param>
    /// <param name="targetExt">目标扩展名</param>
    /// <param name="stagedSource">暂存源文件（排除它自身）</param>
    private static bool HasUsableArtifact(string workDirectory, string targetExt, string stagedSource)
    {
        try
        {
            var expected = Path.Combine(workDirectory, Path.GetFileNameWithoutExtension(stagedSource) + targetExt);
            if (IsUsableArtifact(expected)) return true;
            return Directory.GetFiles(workDirectory)
                .Any(f => string.Equals(Path.GetExtension(f), targetExt, StringComparison.OrdinalIgnoreCase)
                          && !string.Equals(f, stagedSource, StringComparison.OrdinalIgnoreCase)
                          && IsUsableArtifact(f));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Office COM 转换：在专用 STA 线程上晚绑定驱动 Word / PowerPoint / Excel。
    /// 输出文件名由我们指定（不像 LibreOffice 那样跟随源文件名），因此直接写 targetPath。
    /// </summary>
    /// <param name="plan">转换计划</param>
    /// <param name="stagedSource">暂存后的源文件路径</param>
    /// <param name="targetPath">目标临时文件路径</param>
    /// <param name="workDirectory">工作目录（仅用于日志）</param>
    /// <param name="sourceExt">源扩展名</param>
    /// <param name="token">取消令牌</param>
    [SupportedOSPlatform("windows")]
    private static async Task RunOfficeComAsync(DocumentPlan plan, string stagedSource, string targetPath,
        string workDirectory, string sourceExt, CancellationToken token)
    {
        var timeout = EffectiveTimeoutSeconds(stagedSource);
        Log.Info($"[文档] {plan.Description} | 引擎: Office COM | 超时: {timeout}s");

        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(OfficeComConversion.Run(plan, stagedSource, sourceExt, targetPath));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "RenamePro-DocCom"
        };
        // COM 自动化必须在 STA 上运行（MTA 下 Word 会以 RPC_E_* 或事件错误失败）
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var finished = await Task.WhenAny(completion.Task,
            Task.Delay(TimeSpan.FromSeconds(timeout), CancellationToken.None),
            Task.Delay(Timeout.Infinite, token)).ConfigureAwait(false);

        if (finished != completion.Task)
        {
            // 超时或取消：等 COM 线程收尾（它的 finally 会 Close/Quit，把 Office 进程与文件句柄放掉），
            // 但绝不无限等——Office 卡在弹窗上时线程可能永远不返回，那是后台线程，进程退出不受影响。
            thread.Join(TimeSpan.FromSeconds(10));
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException(
                $"文档转换超时（{timeout} 秒，源文件 {Path.GetFileName(stagedSource)}）；" +
                "Office 自动化可能被弹窗阻塞，请检查是否有 Word/PowerPoint 对话框需要处理");
        }

        var result = await completion.Task.ConfigureAwait(false);
        if (result != 0)
        {
            throw new InvalidOperationException($"Office COM 转换失败（结果码 {result}）");
        }
        if (!File.Exists(targetPath))
        {
            throw new InvalidOperationException($"Office COM 未产出目标文件（工作目录：{workDirectory}）");
        }
    }

    /// <summary>
    /// 预热：把 profile 槽位**真正初始化好**。
    ///
    /// 为什么必须做这件事（实测得出）：LibreOffice 用一个全新的用户配置目录跑
    /// `--convert-to` 时，第一次调用会以"初始化未完成"结束且不产出任何文件；
    /// 同一个 profile 的第二次调用才正常。因此预热对每个槽位各跑一次并**丢弃失败的那次**，
    /// 把"初始化"这个代价从用户的第一次转换里挪走——用户侧只看到启动后台安静工作几秒，
    /// 之后第一次转换就直接成功。
    ///
    /// 返回 false 只代表"没预热成功"，不影响功能（真实转换里还有一次自动重试兜底）。
    /// 与正常转换共用同一个 profile 槽位锁，避免两个实例同时写同一份配置。
    /// </summary>
    public static async Task<bool> WarmUpLibreOfficeAsync()
    {
        if (DocumentEngine.SofficePath == null) return false;
        await ProfileSlots.WaitAsync().ConfigureAwait(false);
        try
        {
            return await WarmUpCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            ProfileSlots.Release();
        }
    }

    /// <summary>预热主体（已持有 profile 槽位锁）：把每个槽位的 profile 初始化一遍。</summary>
    private static async Task<bool> WarmUpCoreAsync()
    {
        var slots = Math.Max(1, AppConfig.Current.DocMaxConcurrency) + 1;
        var allOk = true;
        for (var slot = 0; slot < slots; slot++)
        {
            if (!await WarmUpSlotAsync(slot).ConfigureAwait(false)) allOk = false;
        }
        if (allOk) Log.Info($"[文档] 已初始化 {slots} 个 LibreOffice 用户配置目录");
        return allOk;
    }

    /// <summary>初始化一个 profile 槽位：最多试两次，第二次即进入"已初始化"状态。</summary>
    /// <param name="slot">槽位索引</param>
    private static async Task<bool> WarmUpSlotAsync(int slot)
    {
        var workDirectory = Path.Combine(ResolveWarmUpRoot(), $"doc-warmup-{slot}");
        try
        {
            Directory.CreateDirectory(workDirectory);
            var probe = Path.Combine(workDirectory, "warmup.txt");
            File.WriteAllText(probe, "RenamePro document engine warm-up");
            var target = Path.Combine(workDirectory, "warmup.pdf");
            var profileUri = new Uri(EnsureProfileDirectory(slot)).AbsoluteUri;

            for (var attempt = 1; attempt <= 2; attempt++)
            {
                if (File.Exists(target)) File.Delete(target);
                var startInfo = new ProcessStartInfo
                {
                    FileName = DocumentEngine.SofficePath,
                    Arguments = $"-env:UserInstallation={profileUri} --headless --norestore --nolockcheck " +
                                $"--nodefault --nofirststartwizard --nologo " +
                                $"--convert-to \"pdf:writer_pdf_Export\" --outdir {Quote(workDirectory)} {Quote(probe)}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = workDirectory
                };
                // 与正式转换一致：给引擎一个确定可写的临时目录（详见 RunLibreOfficeOnceAsync 的注释）
                var scratch = Path.Combine(workDirectory, "lo-tmp");
                Directory.CreateDirectory(scratch);
                startInfo.Environment["TMP"] = scratch;
                startInfo.Environment["TEMP"] = scratch;
                using var process = Process.Start(startInfo);
                if (process == null) return false;
                var exitTask = process.WaitForExitAsync(CancellationToken.None);
                var done = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(WarmUpTimeoutSeconds)))
                    .ConfigureAwait(false);
                if (done != exitTask)
                {
                    DocumentEngine.TryKill(process);
                    return false;
                }
                // 判据是"产物"，不是退出码：首次初始化的那次通常退出码为 0 却没有产物
                if (File.Exists(target) && new FileInfo(target).Length > 0) return true;
            }
            Log.Info($"[文档] 配置目录 profile-{slot} 预热两次仍未产出结果，首次转换会自动重试");
            return false;
        }
        catch (Exception ex)
        {
            Log.Info($"[文档] 预热异常（profile-{slot}）：{ex.Message}");
            return false;
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
        }
    }

    // ------------------------------------------------------------------ 工作目录与产物

    /// <summary>
    /// 建私有工作目录：必须与目标文件同目录（同卷），最后的 File.Move 才是原子的、瞬时的。
    /// 优先用固定名（便于在用户目录里一眼认出是本程序的残留），上一次运行的残留删不掉时
    /// 自动改用一个带时间戳的新名字，绝不因为"目录已存在"而失败。
    /// </summary>
    /// <param name="parent">父目录（用户文件所在目录）</param>
    private static string CreateWorkDirectoryWithFallback(string parent)
    {
        SweepStaleWorkDirectories(parent); // 先清掉上次运行的残留，再建自己的工作目录
        var preferred = Path.Combine(parent, WorkDirectoryPrefix + Environment.ProcessId);
        try
        {
            Directory.CreateDirectory(preferred);
            return preferred;
        }
        catch (Exception)
        {
            var fallback = Path.Combine(parent,
                $"{WorkDirectoryPrefix}{Environment.ProcessId}-{DateTime.Now:HHmmssfff}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    /// <summary>
    /// 暂存源文件：保留原扩展名（引擎靠扩展名判定输入类型，也是 markdown/txt/html 能被正确识别的唯一依据），
    /// 文件名保持"原名 + Guid"——LibreOffice 会照这个基名命名产物，FindArtifact 正靠这一点优先命中。
    ///
    /// 刻意不用 File.Copy：它会把源文件的属性一起带过来，源文件是只读时暂存副本也是只读，
    /// 转换结束后工作目录就永远删不掉（只读文件连 Directory.Delete(recursive) 都删不动），
    /// 用户的目录里会永久留下一个装着文档副本的隐藏目录。这里显式建文件 + 拷字节流，
    /// 让暂存副本始终是可写的。
    /// </summary>
    /// <param name="sourcePath">源文件（改名后的文件）</param>
    /// <param name="sourceExt">源扩展名</param>
    /// <param name="workDirectory">工作目录</param>
    private static string StageSource(string sourcePath, string sourceExt, string workDirectory)
    {
        var baseName = Sanitize(Path.GetFileNameWithoutExtension(sourcePath));
        var staged = Path.Combine(workDirectory, $"{baseName}-{Guid.NewGuid():N}{sourceExt}");
        IoRetry.Run("暂存源文件", () =>
        {
            using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var destination = new FileStream(staged, FileMode.Create, FileAccess.Write, FileShare.None);
            source.CopyTo(destination);
        });
        return staged;
    }

    /// <summary>清理文件名里不能用的字符（引擎对文件名敏感，中文与空格是安全的）。</summary>
    /// <param name="name">原始文件名（不含扩展名）</param>
    private static string Sanitize(string name)    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
        {
            builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }
        var result = builder.ToString().Trim().Trim('.', ' ');
        return result.Length == 0 ? "doc" : (result.Length > 60 ? result[..60] : result);
    }

    /// <summary>
    /// 在 workDirectory 里找回产物。查找顺序（每一步都要求非空文件）：
    ///   ① 与暂存源同基名 + 目标扩展名 —— LibreOffice 的真实命名规则，也是我们要教会引擎的规则
    ///      （把源暂存成"原名-Guid.docx"，引擎就会输出"原名-Guid.pdf"，FindArtifact 优先命中它）；
    ///   ② 暂存之后新增的目标扩展名文件 —— 兜底：不同引擎/版本的命名规则可能不同，
    ///      只要目录里多出一个目标格式的文件就认它。
    /// 找不到时返回 null，由调用方抛出带目录清单的异常（绝不"假装成功"）。
    /// </summary>
    /// <param name="workDirectory">工作目录</param>
    /// <param name="targetExt">目标扩展名（小写带点）</param>
    /// <param name="stagedSource">暂存源文件路径</param>
    /// <param name="stagedAtUtc">暂存完成时刻（用于过滤旧文件）</param>
    private static string? FindArtifact(string workDirectory, string targetExt, string stagedSource, DateTime stagedAtUtc)
    {
        // ① 预期文件名
        var expectedBase = Path.GetFileNameWithoutExtension(stagedSource);
        var exact = Path.Combine(workDirectory, expectedBase + targetExt);
        if (IsUsableArtifact(exact)) return exact;

        // ② 兜底：暂存之后新出现的目标扩展名文件
        var candidates = Directory.GetFiles(workDirectory)
            .Where(f => string.Equals(Path.GetExtension(f), targetExt, StringComparison.OrdinalIgnoreCase))
            .Where(f => IsUsableArtifact(f))
            .Where(f => File.GetLastWriteTimeUtc(f) >= stagedAtUtc.AddSeconds(-2))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
        if (candidates.Count > 0) return candidates[0];

        return null;
    }

    /// <summary>可用产物判定：存在且非空。</summary>
    /// <param name="path">候选文件路径</param>
    private static bool IsUsableArtifact(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>工作目录内容摘要（产物找不到时写进错误信息，便于排障）。</summary>
    /// <param name="workDirectory">工作目录</param>
    private static string DescribeDirectory(string workDirectory)
    {
        try
        {
            if (!Directory.Exists(workDirectory)) return "工作目录已不存在";
            var files = Directory.GetFiles(workDirectory);
            if (files.Length == 0) return "空";
            return string.Join(", ", files.Select(f => $"{Path.GetFileName(f)}({new FileInfo(f).Length}B)"));
        }
        catch (Exception ex)
        {
            return $"读取失败：{ex.Message}";
        }
    }

    // ------------------------------------------------------------------ profile 槽位

    /// <summary>
    /// 分配一个 profile 槽位：槽位索引用于隔离并发实例的用户配置目录。
    /// 同一时刻同一槽位只被一个进程使用，避免 registrymodifications.xcu 被并发写坏。
    /// 优先返回"已经用成功过"的槽位——理由是实测发现**全新 profile 的第一次调用不会产出结果**，
    /// 复用已初始化的槽位能让后续转换直接命中成功路径。
    /// </summary>
    private static int AcquireProfileSlot()
    {
        lock (ProfileSync)
        {
            if (_provenSlots.Count > 0) return _provenSlots[^1];
            var limit = Math.Max(1, AppConfig.Current.DocMaxConcurrency) + 1;
            var slot = _profileSlotCount % limit;
            _profileSlotCount = slot + 1;
            return slot;
        }
    }

    /// <summary>释放槽位：转换成功过的槽位记入"已验证"，下次优先复用。</summary>
    /// <param name="slot">槽位索引</param>
    /// <param name="succeeded">本次转换是否成功产出了产物</param>
    private static void ReleaseProfileSlot(int slot, bool succeeded)
    {
        lock (ProfileSync)
        {
            if (succeeded && !_provenSlots.Contains(slot)) _provenSlots.Add(slot);
        }
    }

    /// <summary>
    /// 确保指定槽位的 profile 目录存在并返回其路径（槽位隔离并发实例的用户配置）。
    /// </summary>
    /// <param name="slot">槽位索引</param>
    private static string EnsureProfileDirectory(int slot)
    {
        var directory = Path.Combine(ProfileRootLazy.Value, $"profile-{slot}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    // ------------------------------------------------------------------ 超时与工具

    /// <summary>有效超时：配置基准 + 每 MB 3 秒，上限 30 分钟。</summary>
    /// <param name="sourcePath">源文件路径</param>
    public static int EffectiveTimeoutSeconds(string sourcePath)
    {
        var baseSeconds = AppConfig.Current.DocTimeoutSeconds;
        long length = 0;
        try
        {
            length = new FileInfo(sourcePath).Length;
        }
        catch
        {
            // 拿不到大小就只按基准超时
        }
        var megabytes = length / (1024.0 * 1024.0);
        var extra = (int)Math.Ceiling(megabytes * ExtraSecondsPerMegabyte);
        return Math.Clamp(baseSeconds + extra, 30, MaxTimeoutSeconds);
    }

    /// <summary>拼 LibreOffice 的 --convert-to 规格："扩展名[:过滤器[:选项]]"。</summary>
    /// <param name="plan">转换计划</param>
    private static string QuoteFilterSpec(DocumentPlan plan)
    {
        var spec = plan.FilterName.Length == 0
            ? plan.TargetExtForFilter
            : $"{plan.TargetExtForFilter}:{plan.FilterName}";
        if (!string.IsNullOrEmpty(plan.FilterOptions)) spec += ":" + plan.FilterOptions;
        return "\"" + spec + "\"";
    }

    /// <summary>给参数加双引号（Windows 文件名不允许包含引号，无需转义）。</summary>
    /// <param name="value">参数值</param>
    private static string Quote(string value) => "\"" + value + "\"";

    /// <summary>截断过长的引擎输出（日志与错误信息都要保持可读）。</summary>
    /// <param name="text">原始文本</param>
    private static string Trim(string text) =>
        text.Trim().Length > 400 ? text.Trim()[..400] + "…" : text.Trim();

    /// <summary>等待任务结束但不超过指定时长（用于超时后回收进程）。</summary>
    private static async Task WaitQuietlyAsync(Task task, TimeSpan timeout)
    {
        try
        {
            await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        }
        catch
        {
            // 忽略：这里只是尽力回收
        }
    }

    /// <summary>
    /// 尽力删除目录（可能被引擎短暂占用），失败静默。
    /// 重试窗口给到约 3 秒：外置引擎退出后句柄不一定是同步释放的
    /// （Office COM 尤其明显），删早了就会在用户目录里留下一个空壳目录。
    /// 真删不掉时把"还剩什么"写进日志——残留目录是用户可见的，原因必须可查。
    /// </summary>
    /// <param name="directory">目标目录</param>
    private static void TryDeleteDirectory(string directory)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                if (!Directory.Exists(directory)) return;
                // 只读文件会让 Directory.Delete(recursive) 直接拒绝，必须先清属性：
                // 历史版本用 File.Copy 暂存过只读源文件，旧残留目录里的副本就是只读的。
                ClearReadOnly(directory);
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                Thread.Sleep(250);
            }
        }
        string remaining;
        try
        {
            remaining = Directory.Exists(directory)
                ? string.Join(", ", Directory.GetFiles(directory).Select(Path.GetFileName))
                : "（目录已消失）";
        }
        catch (Exception ex)
        {
            remaining = $"（无法枚举：{ex.Message}）";
        }
        Log.Warn($"[文档] 工作目录暂时删不掉，已留在原处：{directory} | 残留文件：{remaining} | 原因：{lastError?.Message}");
    }

    /// <summary>
    /// 清除目录树里的只读属性（删除前的必要步骤，失败忽略）。
    ///
    /// 刻意跳过重解析点（junction / 符号链接）：默认的 AllDirectories 递归会穿过 junction，
    /// 那样就会去改工作目录之外的文件的属性。工作目录是本程序自己建的平铺目录，
    /// 正常不会有重解析点，跳过它零成本。
    /// </summary>
    /// <param name="directory">目标目录</param>
    private static void ClearReadOnly(string directory)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false
        };
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", options))
            {
                try
                {
                    var attributes = File.GetAttributes(file);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                    }
                }
                catch
                {
                    // 单个文件失败不影响整体删除尝试
                }
            }
        }
        catch
        {
            // 枚举失败忽略
        }
    }

    /// <summary>
    /// 清理同一目录里"上一次运行留下"的工作目录。
    ///
    /// 为什么需要它：程序被强制结束（任务管理器结束进程、崩溃、断电）时会跳过 finally 里的清理，
    /// 残留目录里装着用户文档的副本。目录名里带创建它的进程号，因此这里只清理"进程已经不在"的那些，
    /// 绝不碰正在运行的实例（多实例或用户手动运行两份的情况都安全）。
    /// </summary>
    /// <param name="parent">父目录（用户文件所在目录）</param>
    private static void SweepStaleWorkDirectories(string parent)
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(parent, WorkDirectoryPrefix + "*"))
            {
                var name = Path.GetFileName(directory);
                var rest = name[WorkDirectoryPrefix.Length..];
                // 形如 "<pid>" 或 "<pid>-<时间戳>-<guid>"：取第一段数字作为进程号
                var dash = rest.IndexOf('-');
                var pidText = dash < 0 ? rest : rest[..dash];
                if (!int.TryParse(pidText, out var pid)) continue;
                if (pid == Environment.ProcessId) continue;
                if (IsProcessAlive(pid)) continue; // 别的实例正在用，绝不能动
                Log.Info($"[文档] 清理上次运行残留的工作目录：{directory}");
                TryDeleteDirectory(directory);
            }
        }
        catch (Exception ex)
        {
            Log.Info($"[文档] 残留目录清理跳过：{ex.Message}");
        }
    }

    /// <summary>进程号是否仍然存活。</summary>
    /// <param name="pid">进程号</param>
    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            // 取不到进程 = 已经退出
            return false;
        }
    }

    /// <summary>任务的目标扩展名（用于查找产物）。</summary>
    /// <param name="task">任务模型</param>
    private static string TaskExtOf(ConversionTask task) => task.NewExt;

    /// <summary>
    /// Office COM 实际调用（晚绑定 + 专用 STA 线程）。单独放一个嵌套类，
    /// 保证只有真正走 COM 时才加载相关代码路径。
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static class OfficeComConversion
    {
        /// <summary>Word 另存格式码（Office 2007+ 用 16；老版本用 12）。</summary>
        private const int WdFormatDocumentDefault = 16;

        private const int WdFormatDocumentDefaultLegacy = 12;
        private const int WdFormatPdf = 17;
        private const int WdFormatRtf = 6;
        private const int WdFormatText = 2;
        private const int WdFormatHtml = 8;
        private const int WdFormatFilteredHtml = 10;
        private const int WdFormatUnicodeText = 7;

        private const int XlFormatPdf = 0;
        private const int XlFormatXlsx = 51;
        private const int XlFormatXls = 56;
        private const int XlFormatCsv = 6;
        private const int XlFormatText = -4158;

        private const int PptFormatPdf = 2;
        private const int PptFormatOpenXml = 24;
        private const int PptFormatPpt97 = 11;
        private const int PptFormatHtml = 12;

        /// <summary>执行一次 COM 转换，返回 0 表示成功。</summary>
        /// <param name="plan">转换计划</param>
        /// <param name="sourcePath">暂存的源文件</param>
        /// <param name="sourceExt">源扩展名</param>
        /// <param name="targetPath">目标临时文件</param>
        public static int Run(DocumentPlan plan, string sourcePath, string sourceExt, string targetPath)
        {
            // 遗留二进制格式（.doc/.xls/.ppt）与 ODF 由 Office 自己的转换器接管；
            // 只对"Office 原生 + 常见开放格式"直接用对应应用，其余交给 Office 的格式推断。
            var targetExt = Path.GetExtension(targetPath).ToLowerInvariant();
            return plan.SourceFamily switch
            {
                DocumentFamily.Writer => RunWord(sourcePath, targetExt, targetPath),
                DocumentFamily.Impress => RunPowerPoint(sourcePath, targetExt, targetPath),
                DocumentFamily.Calc => RunExcel(sourcePath, targetExt, targetPath),
                _ => throw new InvalidOperationException($"Office COM 不支持 {sourceExt} → {targetExt}")
            };
        }

        /// <summary>Word 转换。</summary>
        private static int RunWord(string sourcePath, string targetExt, string targetPath)
        {
            var app = CreateApplication("Word.Application");
            object? document = null;
            try
            {
                Set(app, "Visible", false);
                Set(app, "DisplayAlerts", 0);
                Set(app, "AutomationSecurity", 3); // msoAutomationSecurityForceDisable：不执行宏
                var documents = Get(app, "Documents")!;
                // Open(FileName, ConfirmConversions=false, ReadOnly=true, AddToRecentFiles=false)
                document = Invoke(documents, "Open", sourcePath, false, true, false);

                if (targetExt == ".pdf")
                {
                    Invoke(document, "ExportAsFixedFormat", targetPath, WdFormatPdf);
                }
                else
                {
                    Invoke(document, "SaveAs2", targetPath, WordFormatFor(targetExt));
                }
                return 0;
            }
            finally
            {
                TryInvoke(document, "Close", 0);
                TryInvoke(app, "Quit", 0);
                Release(document);
                Release(app);
            }
        }

        /// <summary>PowerPoint 转换。</summary>
        private static int RunPowerPoint(string sourcePath, string targetExt, string targetPath)
        {
            var app = CreateApplication("PowerPoint.Application");
            object? presentation = null;
            try
            {
                var presentations = Get(app, "Presentations")!;
                // Open(FileName, ReadOnly, Untitled, WithWindow=false)
                presentation = Invoke(presentations, "Open", sourcePath, -1, 0, 0);
                if (targetExt == ".pdf")
                {
                    Invoke(presentation, "ExportAsFixedFormat", targetPath, PptFormatPdf);
                }
                else
                {
                    Invoke(presentation, "SaveAs", targetPath, PowerPointFormatFor(targetExt));
                }
                return 0;
            }
            finally
            {
                TryInvoke(presentation, "Close");
                TryInvoke(app, "Quit");
                Release(presentation);
                Release(app);
            }
        }

        /// <summary>Excel 转换。</summary>
        private static int RunExcel(string sourcePath, string targetExt, string targetPath)
        {
            var app = CreateApplication("Excel.Application");
            object? workbook = null;
            try
            {
                Set(app, "Visible", false);
                Set(app, "DisplayAlerts", false);
                var workbooks = Get(app, "Workbooks")!;
                // Open(FileName, UpdateLinks=0, ReadOnly=true)
                workbook = Invoke(workbooks, "Open", sourcePath, 0, true);
                if (targetExt == ".pdf")
                {
                    Invoke(workbook, "ExportAsFixedFormat", XlFormatPdf, targetPath);
                }
                else
                {
                    Invoke(workbook, "SaveAs", targetPath, ExcelFormatFor(targetExt));
                }
                return 0;
            }
            finally
            {
                TryInvoke(workbook, "Close", false);
                TryInvoke(app, "Quit");
                Release(workbook);
                Release(app);
            }
        }

        /// <summary>Word 的另存格式码；不认识的扩展名交给 Word 的默认格式。</summary>
        private static int WordFormatFor(string targetExt) => targetExt switch
        {
            ".docx" => WdFormatDocumentDefault,
            ".doc" => 0,
            ".rtf" => WdFormatRtf,
            ".txt" => WdFormatUnicodeText,
            ".html" or ".htm" => WdFormatFilteredHtml,
            ".odt" => -1, // 交给 Word 的转换器推断
            _ => WdFormatDocumentDefault
        };

        /// <summary>PowerPoint 的另存格式码。</summary>
        private static int PowerPointFormatFor(string targetExt) => targetExt switch
        {
            ".pptx" => PptFormatOpenXml,
            ".ppt" => PptFormatPpt97,
            ".html" or ".htm" => PptFormatHtml,
            ".rtf" => 5,
            ".txt" => 4,
            _ => PptFormatOpenXml
        };

        /// <summary>Excel 的另存格式码。</summary>
        private static int ExcelFormatFor(string targetExt) => targetExt switch
        {
            ".xlsx" => XlFormatXlsx,
            ".xls" => XlFormatXls,
            ".csv" => XlFormatCsv,
            ".tsv" => XlFormatText,
            _ => XlFormatXlsx
        };

        /// <summary>按 ProgID 建应用实例；未注册时抛可读异常。</summary>
        private static object CreateApplication(string progId)
        {
            var type = Type.GetTypeFromProgID(progId)
                       ?? throw new InvalidOperationException($"Office COM 不可用：未注册 {progId}");
            return Activator.CreateInstance(type)
                   ?? throw new InvalidOperationException($"{progId} 实例创建失败");
        }

        /// <summary>晚绑定读属性；属性取不到时抛异常（调用方拿到的都是有效对象，避免到处写空检查）。</summary>
        private static object Get(object target, string name) =>
            target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null)
            ?? throw new InvalidOperationException($"Office 对象缺少属性 {name}");

        /// <summary>晚绑定写属性。</summary>
        private static void Set(object target, string name, object? value) =>
            target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, new[] { value });

        /// <summary>
        /// 晚绑定调用方法（带瞬时拒绝重试）。
        /// target 声明为可空是刻意的：调用方用强类型局部变量接住 COM 对象，
        /// 编译器按"可能为 null"分析，这里统一兜底成可读异常。
        /// </summary>
        private static object? Invoke(object? target, string name, params object?[] arguments)
        {
            if (target == null) throw new InvalidOperationException($"Office 对象为 null，无法调用 {name}");
            var retries = Math.Clamp(AppConfig.Current.DocComRetries, 0, 5);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, arguments);
                }
                catch (COMException ex) when (attempt < retries && IsTransient(ex))
                {
                    RetryNotice(name, ex, attempt, retries);
                }
                catch (TargetInvocationException ex) when (attempt < retries && ex.InnerException is COMException inner && IsTransient(inner))
                {
                    RetryNotice(name, inner, attempt, retries);
                }
            }
        }

        /// <summary>记录一次重试（0x80010001 = RPC_E_CALL_REJECTED：目标应用正忙，稍后重试即可成功）。</summary>
        private static void RetryNotice(string name, COMException ex, int attempt, int retries)
        {
            Log.Info($"[文档] COM 调用 {name} 被目标应用暂时拒绝（0x{ex.HResult:X8}），500ms 后重试（{attempt + 1}/{retries}）");
            Thread.Sleep(500);
        }

        /// <summary>是不是"目标应用正忙"这类瞬时错误（稍后重试通常就能成功）。</summary>
        /// <param name="ex">COM 异常</param>
        private static bool IsTransient(COMException ex) =>
            ex.HResult == unchecked((int)0x80010001)   // RPC_E_CALL_REJECTED
            || ex.HResult == unchecked((int)0x8001010A) // RPC_E_SERVERCALL_RETRYLATER
            || ex.HResult == unchecked((int)0x80010005);

        /// <summary>尽力调用（清理路径用，失败不影响结果判定）。</summary>
        private static void TryInvoke(object? target, string name, params object?[] arguments)
        {
            if (target == null) return;
            try
            {
                Invoke(target, name, arguments);
            }
            catch
            {
                // 忽略：清理失败只影响内存回收，不影响产物
            }
        }

        /// <summary>释放 COM 引用。</summary>
        private static void Release(object? target)
        {
            if (target == null) return;
            try
            {
                if (Marshal.IsComObject(target)) Marshal.ReleaseComObject(target);
            }
            catch
            {
                // 忽略
            }
        }
    }
}
