using System.Collections.Concurrent;
using RenamePro.Core;

namespace RenamePro.Conversion;

/// <summary>
/// 转换主流程：把一次“扩展名改名”从校验到替换完整串联。
/// 流程：防重入 → 特殊文件 → magic 校验 → 探测分类 → 入队（备份 → 转换 → 原子替换）。
/// 里程碑 2 阶段进度暂用日志代替进度对话框。
/// </summary>
public sealed class ConversionPipeline : IDisposable
{
    /// <summary>处理中路径表（防重入：同一路径处理期间的重复触发直接跳过）。</summary>
    private static readonly ConcurrentDictionary<string, byte> ProcessingPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>分级调度队列（快速 2 / 图片 2 / 音视频 1）。</summary>
    private readonly ConversionScheduler _scheduler = new();

    /// <summary>内部操作抑制表（替换前登记，防本程序自己触发的改名被再次转换）。</summary>
    private readonly InternalOpsSet _internalOps;

    /// <summary>ffmpeg 缺失的警告只记一次。</summary>
    private bool _avUnavailableLogged;

    /// <summary>
    /// 创建转换主流程。
    /// </summary>
    /// <param name="internalOps">内部操作抑制表（与监听器共用同一实例）</param>
    public ConversionPipeline(InternalOpsSet internalOps) => _internalOps = internalOps;

    /// <summary>
    /// 提交一次转换任务（防抖到期后由监听器回调，线程池线程）。
    /// </summary>
    /// <param name="oldPath">改名前路径</param>
    /// <param name="newPath">改名后路径</param>
    public void Submit(string oldPath, string newPath)
    {
        // 防重入：处理中的路径再次触发直接跳过
        if (!ProcessingPaths.TryAdd(newPath, 0))
        {
            Log.Info($"[结果] SKIPPED | 旧: {oldPath} | 新: {newPath} | 原因: 该路径正在处理中（防重入）");
            return;
        }
        _ = RunSafeAsync(new ConversionTask(oldPath, newPath));
    }

    /// <summary>安全执行：统一兜底异常并移除防重入标记。</summary>
    private async Task RunSafeAsync(ConversionTask task)
    {
        try
        {
            await ProcessAsync(task).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error($"[结果] FAILED | 旧: {task.OldPath} | 新: {task.NewPath} | 错误: {ex.Message}");
        }
        finally
        {
            ProcessingPaths.TryRemove(task.NewPath, out _);
        }
    }

    /// <summary>主流程：校验 → 探测分类 → 入队。</summary>
    private async Task ProcessAsync(ConversionTask task)
    {
        // —— 特殊文件直接跳过 ——
        FileAttributes attributes;
        try
        {
            attributes = IoRetry.Run("读取文件属性", () => File.GetAttributes(task.NewPath));
        }
        catch (FileNotFoundException)
        {
            LogSkip(task, "文件已不存在（改名后又被移动/删除）");
            return;
        }
        catch (DirectoryNotFoundException)
        {
            LogSkip(task, "所在目录已不存在");
            return;
        }
        if ((attributes & FileAttributes.Encrypted) != 0)
        {
            LogSkip(task, "EFS 加密文件");
            return;
        }
        // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS（0x400000）：云盘按需占位文件。
        // 该位未收录进 .NET 的 FileAttributes 枚举，按原始属性位判断；
        // skipCloudFiles 固定启用（里程碑 4 起由 config.json 提供）
        const int recallOnDataAccessBit = 0x400000;
        if (((int)attributes & recallOnDataAccessBit) != 0)
        {
            LogSkip(task, "云盘按需文件（RecallOnDataAccess）");
            return;
        }

        // —— 统一前置校验：magic number 确认真实格式与源（旧）扩展名一致 ——
        var format = IoRetry.Run("读取文件头", () => FormatSniffer.Sniff(task.NewPath));
        if (!FormatSniffer.MatchesExtension(format, task.OldExt))
        {
            LogSkip(task, $"magic 不符：内容实为 {format ?? "未知格式"}，源扩展名 {task.OldExt}");
            return;
        }

        // —— 分类入队 ——
        ConversionLane lane;
        Func<CancellationToken, Task> body;

        if (PathRules.IsImageExtension(task.NewExt))
        {
            // 图片：< 2MB 进快速通道，其余进图片队列
            var size = IoRetry.Run("读取文件长度", () => new FileInfo(task.NewPath).Length);
            lane = size < 2L * 1024 * 1024 ? ConversionLane.Fast : ConversionLane.Image;
            task.Progress = new QuarterProgress(task.Description);
            body = ct => RunJobAsync(task,
                (temp, token) =>
                {
                    // Magick 转换为同步 API，占用通道槽执行即可
                    ImageConverter.Convert(task, task.NewPath, temp, token);
                    return Task.CompletedTask;
                }, ct);
        }
        else if (PathRules.IsAudioVideoExtension(task.NewExt))
        {
            // ffmpeg 缺失：仅禁用音视频功能，图片不受影响
            if (!AvConverter.IsAvailable)
            {
                if (!_avUnavailableLogged)
                {
                    Log.Warn("ffmpeg.exe / ffprobe.exe 缺失，音视频转换功能已禁用（图片功能不受影响）");
                    _avUnavailableLogged = true;
                }
                LogSkip(task, "ffmpeg.exe 缺失，音视频转换已禁用");
                return;
            }

            // ffprobe 探测 → 流拷贝优先决策
            var probe = AvConverter.Probe(task.NewPath);
            var plan = AvConverter.BuildPlan(task, probe);
            if (plan == null)
            {
                LogSkip(task, "不在支持矩阵内的扩展名组合");
                return;
            }
            // 流拷贝进快速通道（秒级）；重编码进音视频队列（全局串行）
            lane = plan.Kind == AvPlanKind.StreamCopy ? ConversionLane.Fast : ConversionLane.AudioVideo;
            task.Progress = new QuarterProgress(task.Description);
            body = ct => RunJobAsync(task,
                (temp, token) => AvConverter.ExecuteAsync(task, plan, probe, task.NewPath, temp, token), ct);
        }
        else
        {
            LogSkip(task, "扩展名不在图片/音视频集合");
            return;
        }

        Log.Info($"[入队] {task.Description} | 通道: {lane}");
        await _scheduler.RunAsync(lane, body, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// 任务体（在通道槽内执行）：备份 → 转换 → 原子替换。
    /// 顺序硬约束：复制完成后再开始转换。
    /// </summary>
    private async Task RunJobAsync(ConversionTask task, Func<string, CancellationToken, Task> convertAsync, CancellationToken token)
    {
        string? backupPath = null;
        string? tempPath = null;
        try
        {
            // ① 备份先行：把改名后的新文件原样字节复制为“原格式副本”（此刻内容仍是旧格式，复制即备份）
            backupPath = BackupManager.CreateBackup(task.NewPath, task.OldExt);
            Log.Info($"[备份] {task.Description} | 副本: {backupPath}");
            token.ThrowIfCancellationRequested();

            // ② 转换结果写入同目录临时文件（Guid 命名 + 新扩展名）
            tempPath = Path.Combine(Path.GetDirectoryName(task.NewPath)!,
                Guid.NewGuid().ToString("N") + task.NewExt);
            await convertAsync(tempPath, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            // ③ 替换：目标只读时先临时清除只读位；替换前登记 InternalOpsSet（防 File.Replace 触发的改名事件回流）
            var attributes = File.GetAttributes(task.NewPath);
            var wasReadOnly = (attributes & FileAttributes.ReadOnly) != 0;
            if (wasReadOnly)
                IoRetry.Run("临时清除只读位", () => File.SetAttributes(task.NewPath, attributes & ~FileAttributes.ReadOnly));
            try
            {
                _internalOps.Register(tempPath);
                _internalOps.Register(task.NewPath);
                IoRetry.Run("原子替换", () => File.Replace(tempPath, task.NewPath, null, true));
                tempPath = null; // 替换成功后临时文件已不存在
            }
            finally
            {
                if (wasReadOnly)
                    IoRetry.Run("恢复只读位",
                        () => File.SetAttributes(task.NewPath, File.GetAttributes(task.NewPath) | FileAttributes.ReadOnly));
            }

            Log.Info($"[结果] OK | 旧: {task.OldPath} | 新: {task.NewPath} | 副本: {backupPath} | 耗时: {task.ElapsedSeconds:0.0}s");
        }
        catch (OperationCanceledException)
        {
            // 取消：清理临时文件，副本保留
            TryDelete(tempPath);
            Log.Info($"[结果] CANCELLED | 旧: {task.OldPath} | 新: {task.NewPath} | 副本保留: {backupPath}");
        }
        catch (Exception ex)
        {
            // 失败：删除临时文件并标记任务失败（默认保守策略：不回滚）
            TryDelete(tempPath);
            Log.Error($"[结果] FAILED | 旧: {task.OldPath} | 新: {task.NewPath} | 副本: {backupPath} | 错误: {ex.Message}");
        }
    }

    /// <summary>记录一条 SKIPPED 结果日志。</summary>
    private static void LogSkip(ConversionTask task, string reason) =>
        Log.Info($"[结果] SKIPPED | 旧: {task.OldPath} | 新: {task.NewPath} | 原因: {reason}");

    /// <summary>尽力删除临时文件（失败静默忽略，不影响结果判定）。</summary>
    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 删除失败不改变任务结果
        }
    }

    /// <summary>释放调度队列。</summary>
    public void Dispose() => _scheduler.Dispose();

    /// <summary>
    /// 进度日志器：里程碑 2 用日志代替进度框，只在跨过 25% 档位时写日志（防刷屏）；
    /// -1 表示进度未知。
    /// </summary>
    private sealed class QuarterProgress : IProgress<int>
    {
        /// <summary>状态文案（如“正在转换 a.jpg → a.png”）。</summary>
        private readonly string _label;

        /// <summary>上次记录的档位（-1 起步）。</summary>
        private int _lastQuarter = -1;

        /// <summary>创建进度日志器。</summary>
        /// <param name="label">状态文案</param>
        public QuarterProgress(string label) => _label = label;

        /// <summary>上报百分比；跨档才落日志。</summary>
        /// <param name="value">0~100，或 -1 表示进度未知</param>
        public void Report(int value)
        {
            if (value < 0)
            {
                if (_lastQuarter != -2)
                {
                    _lastQuarter = -2;
                    Log.Info($"[进度] {_label}（进度未知）");
                }
                return;
            }
            var quarter = value / 25;
            if (quarter <= _lastQuarter) return;
            _lastQuarter = quarter;
            Log.Info($"[进度] {_label} {value}%");
        }
    }
}