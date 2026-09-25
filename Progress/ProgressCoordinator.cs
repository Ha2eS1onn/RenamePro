using RenamePro.Conversion;
using RenamePro.Core;
using RenamePro.Interop;

namespace RenamePro.Progress;

/// <summary>
/// 进度聚合器：把多个转换任务聚合到单一系统进度对话框（外观同资源管理器传输框）。
/// 关键行为：
/// 1) 首个任务开始后 400ms 仍未完成才打开对话框（预计耗时小于 400ms 的任务静默完成，防闪烁）；
/// 2) 队列清空后立即关闭对话框；
/// 3) 已知总量显示百分比，未知总量切换滚动条/速度模式；
/// 4) 对话框“取消”→ 取消全部队列（进行中的 ffmpeg 进程树被杀、副本保留）；
/// 5) Shell COM 任一步失败（或环境变量 RENAMEPRO_FORCE_DIALOG_FAIL=1 伪造失败）→
///    本会话降级为静默转换 + 完成后 Toast 汇总（Toast 不可用时回退托盘图标闪烁）。
/// 所有 Shell COM 调用只经 <see cref="StaDispatcher"/> 在专用 STA 消息线程上执行。
/// </summary>
public sealed class ProgressCoordinator : IDisposable
{
    /// <summary>防闪烁延迟：任务开始后 400ms 内完成的批次不打开对话框。</summary>
    private static readonly TimeSpan ShowDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>取消轮询间隔（检测用户点击对话框“取消”）。</summary>
    private static readonly TimeSpan CancelPollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>伪造 COM 初始化失败的环境变量名（验收用）。</summary>
    private const string ForceFailEnvVar = "RENAMEPRO_FORCE_DIALOG_FAIL";

    /// <summary>每任务折算的进度点数。</summary>
    private const ulong PointsPerTask = 100;

    /// <summary>进度尚未上报（任务刚切换，保持上一状态显示）。</summary>
    private const int ProgressNotReported = -2;

    /// <summary>进度未知（如流拷贝无总时长 → 滚动条/速度模式）。</summary>
    private const int ProgressUnknown = -1;

    /// <summary>转换主流程（事件来源 + 取消入口）。</summary>
    private readonly ConversionPipeline _pipeline;

    /// <summary>对话框拥有者窗口句柄（隐藏窗口，可为 IntPtr.Zero）。</summary>
    private readonly IntPtr _ownerHandle;

    /// <summary>提示回退动作（托盘图标闪烁）。</summary>
    private readonly Action _attentionFallback;

    /// <summary>状态锁（批次计数与开关）。</summary>
    private readonly object _sync = new();

    /// <summary>本批次已入队未结束的任务（保持入队顺序，用于“第 x 个”）。</summary>
    private readonly List<ConversionTask> _pendingTasks = new();

    /// <summary>专用 STA 消息线程（惰性创建）。</summary>
    private StaDispatcher? _dispatcher;

    /// <summary>当前进度对话框（仅 STA 线程访问）。</summary>
    private volatile ShellProgressDialog? _dialog;

    /// <summary>是否已降级为静默转换（本会话内不再尝试 COM）。</summary>
    private bool _degraded;

    /// <summary>是否已安排 400ms 后的打开动作。</summary>
    private bool _openScheduled;

    /// <summary>批次是否进行中。</summary>
    private bool _batchActive;

    /// <summary>本批次任务总数。</summary>
    private int _total;

    /// <summary>成功数。</summary>
    private int _succeeded;

    /// <summary>失败数。</summary>
    private int _failed;

    /// <summary>跳过数。</summary>
    private int _skipped;

    /// <summary>取消数。</summary>
    private int _cancelled;

    /// <summary>当前显示的任务。</summary>
    private ConversionTask? _currentTask;

    /// <summary>当前任务的最近百分比（-2 未上报 / -1 未知 / 0~100）。</summary>
    private int _lastPercent = ProgressNotReported;

    /// <summary>延迟打开定时器（防闪烁）。</summary>
    private System.Threading.Timer? _openTimer;

    /// <summary>取消轮询定时器。</summary>
    private System.Threading.Timer? _cancelPollTimer;

    /// <summary>是否已释放。</summary>
    private volatile bool _disposed;

    /// <summary>
    /// 创建进度聚合器并订阅主流程事件。
    /// </summary>
    /// <param name="pipeline">转换主流程</param>
    /// <param name="ownerHandle">对话框拥有者窗口句柄（隐藏窗口句柄）</param>
    /// <param name="attentionFallback">提示回退动作（托盘图标闪烁）</param>
    public ProgressCoordinator(ConversionPipeline pipeline, IntPtr ownerHandle, Action attentionFallback)
    {
        _pipeline = pipeline;
        _ownerHandle = ownerHandle;
        _attentionFallback = attentionFallback;
        pipeline.TaskEnqueued += OnTaskEnqueued;
        pipeline.TaskProgressChanged += OnTaskProgressChanged;
        pipeline.TaskFinished += OnTaskFinished;
    }

    /// <summary>释放：退订事件、关闭对话框与 STA 线程。</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _pipeline.TaskEnqueued -= OnTaskEnqueued;
        _pipeline.TaskProgressChanged -= OnTaskProgressChanged;
        _pipeline.TaskFinished -= OnTaskFinished;
        _openTimer?.Dispose();
        _cancelPollTimer?.Dispose();
        var dispatcher = _dispatcher;
        if (dispatcher == null) return;
        dispatcher.BeginInvoke(() =>
        {
            var dialog = _dialog;
            _dialog = null;
            dialog?.Dispose();
        });
        dispatcher.Dispose();
    }

    /// <summary>任务入队：累计批次、必要时安排 400ms 后打开对话框。</summary>
    private void OnTaskEnqueued(ConversionTask task)
    {
        lock (_sync)
        {
            if (_disposed) return;
            _total++;
            _pendingTasks.Add(task);
            _batchActive = true;
            _currentTask ??= task;
            // showProgressDialog=false：完全不初始化 Shell COM（静默转换 + 完成 Toast）
            if (!AppConfig.Current.ShowProgressDialog || _degraded || _openScheduled || _dialog != null) return;
            // 400ms 后仍未结束才显示：短任务静默完成，避免进度框闪烁
            _openScheduled = true;
            _openTimer ??= new System.Threading.Timer(_ => TryOpenDialog(), null, Timeout.Infinite, Timeout.Infinite);
            _openTimer.Change(ShowDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>400ms 到点：批次仍在进行则请求 STA 线程打开对话框。</summary>
    private void TryOpenDialog()
    {
        lock (_sync)
        {
            if (_disposed || _degraded || !_batchActive)
            {
                _openScheduled = false;
                return;
            }
        }
        var dispatcher = EnsureDispatcher();
        if (dispatcher == null)
        {
            MarkDegraded("专用 STA 消息线程创建失败");
            return;
        }
        dispatcher.BeginInvoke(OpenDialogOnStaThread);
    }

    /// <summary>在 STA 线程上创建并显示对话框（失败则降级）。</summary>
    private void OpenDialogOnStaThread()
    {
        lock (_sync)
        {
            _openScheduled = false;
            if (_disposed || _degraded || !_batchActive || _dialog != null) return;
        }
        if (IsForceFailure())
        {
            MarkDegraded($"已通过环境变量 {ForceFailEnvVar} 伪造 COM 初始化失败");
            return;
        }

        var dialog = new ShellProgressDialog();
        if (!dialog.Start(_ownerHandle))
        {
            dialog.Dispose();
            // 拥有者窗口不被接受时，再以“无拥有者”重试一次
            if (_ownerHandle != IntPtr.Zero)
            {
                Log.Info("[进度] 改用无拥有者窗口重试启动进度对话框…");
                var retry = new ShellProgressDialog();
                if (retry.Start(IntPtr.Zero))
                {
                    _dialog = retry;
                    Log.Info("[进度] 已打开系统进度对话框（无拥有者模式）");
                    ApplyStateToDialog();
                    StartCancelPolling();
                    return;
                }
                retry.Dispose();
            }
            MarkDegraded("进度对话框启动失败");
            return;
        }
        _dialog = dialog;
        Log.Info("[进度] 已打开系统进度对话框（资源管理器传输框同款）");
        ApplyStateToDialog(); // 立即写入首次状态（对话框需要一次更新才渲染）
        StartCancelPolling();
    }

    /// <summary>任务进度变化：只显示当前任务，值变化才刷新（在 STA 线程执行）。</summary>
    private void OnTaskProgressChanged(ConversionTask task, int percent)
    {
        if (_disposed) return;
        lock (_sync)
        {
            if (_degraded || _dialog == null) return; // 降级/未显示：日志已由主流程记录
            if (!ReferenceEquals(_currentTask, task)) return;
            if (percent == _lastPercent) return;
            _lastPercent = percent;
        }
        _dispatcher?.BeginInvoke(ApplyStateToDialog);
    }

    /// <summary>把当前批次状态写入对话框（聚合进度：已完成任务数 + 当前任务内百分比）。</summary>
    private void ApplyStateToDialog()
    {
        var dialog = _dialog;
        if (dialog == null) return;

        ConversionTask? current;
        int total, finished, percent;
        lock (_sync)
        {
            current = _currentTask;
            total = _total;
            finished = _succeeded + _failed + _skipped + _cancelled;
            percent = _lastPercent;
        }

        if (current != null) dialog.SetLocations(current.NewPath);
        if (total <= 0) return;

        var totalPoints = (ulong)Math.Max(total, 1) * PointsPerTask;
        var currentPoints = (ulong)finished * PointsPerTask;
        if (percent == ProgressUnknown)
        {
            // 未知总量（如流拷贝无总时长）→ 滚动条/速度模式
            dialog.SetIndeterminate();
        }
        else
        {
            // 确定进度；-2（任务刚切换尚未上报）只显示已完成任务的基线，避免频繁切换显示模式
            dialog.SetNormal();
            if (percent > 0) currentPoints += (ulong)Math.Clamp(percent, 0, 100);
        }
        dialog.UpdateProgress(Math.Min(currentPoints, totalPoints), totalPoints);
    }

    /// <summary>任务结束：计数、切换当前任务；批次结束则关闭对话框并汇总。</summary>
    private void OnTaskFinished(ConversionTask task, ConversionResult result)
    {
        bool drained;
        lock (_sync)
        {
            // 只统计真正入队过的任务（入队前即被跳过的任务不计入本批次展示）
            if (!_pendingTasks.Remove(task)) return;
            switch (result)
            {
                case ConversionResult.Ok: _succeeded++; break;
                case ConversionResult.Failed: _failed++; break;
                case ConversionResult.Cancelled: _cancelled++; break;
                default: _skipped++; break;
            }
            if (ReferenceEquals(_currentTask, task))
            {
                _currentTask = null;
                _lastPercent = ProgressNotReported;
            }
            _currentTask ??= _pendingTasks.Count > 0 ? _pendingTasks[0] : null;
            drained = _pendingTasks.Count == 0;
            if (drained)
            {
                _batchActive = false;
            }
            else if (ReferenceEquals(_currentTask, null) == false)
            {
                _lastPercent = ProgressNotReported; // 切换任务：重置为“未上报”
            }
        }

        if (_disposed) return;
        if (drained) OnBatchDrained();
        else if (_dialog != null) _dispatcher?.BeginInvoke(ApplyStateToDialog);
    }

    /// <summary>批次结束：关闭对话框（或降级模式下弹 Toast 汇总）。</summary>
    private void OnBatchDrained()
    {
        // 取消尚未触发的延迟打开（短任务批次不闪进度框）
        _openTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        StopCancelPolling();

        int total, succeeded, failed, skipped, cancelled;
        lock (_sync)
        {
            total = _total;
            succeeded = _succeeded;
            failed = _failed;
            skipped = _skipped;
            cancelled = _cancelled;
            // 重置批次，等待下一批
            _total = 0;
            _succeeded = 0;
            _failed = 0;
            _skipped = 0;
            _cancelled = 0;
            _openScheduled = false;
            _lastPercent = ProgressNotReported;
        }

        if (_dialog != null)
        {
            _dispatcher?.BeginInvoke(CloseDialogOnStaThread);
            Log.Info($"[进度] 批次结束：共 {total} 个（成功 {succeeded} / 失败 {failed} / 跳过 {skipped} / 取消 {cancelled}）");
        }
        else if (total <= 0)
        {
            return;
        }

        // enableToast=true：每批任务完成后都弹 Toast 汇总（含降级模式与关闭进度框的场景）
        if (AppConfig.Current.EnableToast) ShowSummaryToast(total, succeeded, failed, skipped, cancelled);
    }

    /// <summary>在 STA 线程上关闭并释放对话框。</summary>
    private void CloseDialogOnStaThread()
    {
        var dialog = _dialog;
        _dialog = null;
        if (dialog == null) return;
        dialog.Stop();
        dialog.Dispose();
        Log.Info("[进度] 进度对话框已关闭");
    }

    /// <summary>启动取消轮询（检测用户点击对话框“取消”或关闭对话框）。</summary>
    private void StartCancelPolling()
    {
        _cancelPollTimer ??= new System.Threading.Timer(_ => PollCancel(), null, Timeout.Infinite, Timeout.Infinite);
        _cancelPollTimer.Change(CancelPollInterval, CancelPollInterval);
    }

    /// <summary>停止取消轮询。</summary>
    private void StopCancelPolling() => _cancelPollTimer?.Change(Timeout.Infinite, Timeout.Infinite);

    /// <summary>轮询对话框状态：仅 PDOPS_CANCELLED 视为用户取消（在 STA 线程读取）。</summary>
    private void PollCancel()
    {
        if (_disposed || _dialog == null) return;
        _dispatcher?.BeginInvoke(() =>
        {
            var dialog = _dialog;
            if (dialog == null) return;
            var status = dialog.GetStatus();
            // 正常运行/暂停：继续等待
            if (status == (int)PdOpStatus.Running || status == (int)PdOpStatus.Paused) return;

            if (status == (int)PdOpStatus.Cancelled)
            {
                StopCancelPolling();
                Log.Info("[进度] 用户在进度对话框中取消：正在取消全部队列（副本保留、无残留进程）");
                CloseDialogOnStaThread();
                _pipeline.CancelAll();
                return;
            }

            // 其它状态（STOPPED / 读取失败 / 对话框被关闭）：只收尾对话框，不取消队列
            StopCancelPolling();
            Log.Info($"[进度] 对话框状态={status}，视为已关闭（不取消转换队列）");
            CloseDialogOnStaThread();
        });
    }

    /// <summary>标记降级（本会话不再尝试 Shell COM），写入日志。</summary>
    private void MarkDegraded(string reason)
    {
        lock (_sync)
        {
            if (_degraded) return;
            _degraded = true;
            _openScheduled = false;
        }
        Log.Warn($"[进度] 已降级为静默转换（完成后弹 Toast 汇总）：{reason}");
    }

    /// <summary>惰性创建专用 STA 消息线程。</summary>
    private StaDispatcher? EnsureDispatcher()
    {
        lock (_sync)
        {
            if (_disposed) return null;
            if (_dispatcher != null) return _dispatcher;
            try
            {
                _dispatcher = new StaDispatcher();
            }
            catch (Exception ex)
            {
                Log.Error($"[进度] STA 消息线程创建失败：{ex.Message}");
                return null;
            }
            return _dispatcher;
        }
    }

    /// <summary>是否通过环境变量伪造 COM 初始化失败（验收测试用）。</summary>
    private static bool IsForceFailure()
    {
        var value = Environment.GetEnvironmentVariable(ForceFailEnvVar);
        return !string.IsNullOrEmpty(value) && value != "0";
    }

    /// <summary>降级模式下的完成汇总：Toast（失败行带“失败：”前缀），Toast 不可用则托盘闪烁兜底。</summary>
    private void ShowSummaryToast(int total, int succeeded, int failed, int skipped, int cancelled)
    {
        var lines = new List<string>
        {
            $"共 {total} 个任务：成功 {succeeded} / 失败 {failed} / 跳过 {skipped} / 取消 {cancelled}"
        };
        if (failed > 0) lines.Add($"失败：{failed} 个（详见 log.txt）");

        var shown = ToastService.ShowSummary("RenamePro 转换完成", lines);
        if (!shown) _attentionFallback(); // Toast 不可见时用托盘闪烁保证有反馈
        Log.Info($"[进度] Toast 汇总已发送（显示{(shown ? "成功" : "失败，已回退托盘闪烁")}）：共 {total} 个，成功 {succeeded}，失败 {failed}，跳过 {skipped}，取消 {cancelled}");
    }
}