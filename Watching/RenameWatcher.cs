using RenamePro.Core;

namespace RenamePro.Watching;

/// <summary>
/// 文件改名监听器：为每个固定磁盘各创建一个 FileSystemWatcher 监听 Renamed 事件。
/// 性能要点：NotifyFilter 仅 FileName（Renamed 只依赖文件名通知，绝不保留默认全集）、
/// 缓冲区 256KB、IncludeSubdirectories；Error 时按 1s / 5s / 30s 指数退避重建。
/// </summary>
public sealed class RenameWatcher : IDisposable
{
    /// <summary>重建退避序列：1 秒 → 5 秒 → 30 秒。</summary>
    private static readonly TimeSpan[] RebuildDelays =
    {
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)
    };

    /// <summary>watcher 级同步锁（重建 / 暂停 / 释放互斥）。</summary>
    private readonly object _sync = new();

    /// <summary>内部操作抑制表检查点（本程序自身引起的改名在此丢弃，防死循环）。</summary>
    private readonly InternalOpsSet _internalOps;

    /// <summary>300ms 防抖合并调度器。</summary>
    private readonly DebounceScheduler _debounce;

    /// <summary>当前所有盘符的 watcher 实例。</summary>
    private readonly List<FileSystemWatcher> _watchers = new();

    /// <summary>连续错误计数，用于指数退避；收到事件后归零。</summary>
    private int _errorStreak;

    /// <summary>最近一次收到事件的时间（UTC），用于记录缓冲区溢出的丢失时间窗口。</summary>
    private DateTime _lastEventUtc = DateTime.UtcNow;

    /// <summary>是否处于暂停状态。</summary>
    private bool _paused;

    /// <summary>是否已释放。</summary>
    private bool _disposed;

    /// <summary>
    /// 创建监听器并立即开始监听所有固定磁盘。
    /// </summary>
    /// <param name="internalOps">内部操作抑制表</param>
    public RenameWatcher(InternalOpsSet internalOps)
    {
        _internalOps = internalOps;
        _debounce = new DebounceScheduler(OnDebounceDue);
        lock (_sync)
        {
            StartAllWatchers();
        }
    }

    /// <summary>当前是否已暂停监听。</summary>
    public bool IsPaused
    {
        get { lock (_sync) return _paused; }
    }

    /// <summary>暂停所有监听（托盘菜单“暂停监听”）。</summary>
    public void Pause()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _paused = true;
            foreach (var watcher in _watchers) watcher.EnableRaisingEvents = false;
        }
        Log.Info("[监听] 已暂停监听");
    }

    /// <summary>恢复所有监听（托盘菜单“继续监听”）。</summary>
    public void Resume()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _paused = false;
            if (_watchers.Count == 0)
            {
                // 之前的 watcher 全部创建/重建失败时兜底重来
                StartAllWatchers();
            }
            else
            {
                foreach (var watcher in _watchers) watcher.EnableRaisingEvents = true;
            }
        }
        Log.Info("[监听] 已恢复监听");
    }

    /// <summary>为每个“已就绪的固定磁盘”创建一个 watcher 并启用。</summary>
    private void StartAllWatchers()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            // 仅监听固定磁盘（U 盘 / 移动硬盘等可移动磁盘不监听）
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            var root = drive.RootDirectory.FullName;
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    Filter = "*.*",
                    // 性能关键：Renamed 只依赖文件名通知，不要保留默认全集
                    NotifyFilter = NotifyFilters.FileName,
                    IncludeSubdirectories = true,
                    InternalBufferSize = 256 * 1024, // 256KB
                };
                watcher.Renamed += OnRenamed;
                watcher.Error += OnError;
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
                Log.Info($"[监听] 开始监听固定磁盘 {root}");
            }
            catch (Exception ex)
            {
                Log.Error($"[监听] 监听 {root} 失败：{ex.Message}");
            }
        }
    }

    /// <summary>释放全部 watcher（重建前先清干净）。</summary>
    private void DisposeAllWatchers()
    {
        foreach (var watcher in _watchers)
        {
            watcher.Renamed -= OnRenamed;
            watcher.Error -= OnError;
            watcher.Dispose();
        }
        _watchers.Clear();
    }

    /// <summary>Renamed 回调：首行内部操作检查 → 早退判定 → 防抖合并。</summary>
    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        _lastEventUtc = DateTime.UtcNow;
        _errorStreak = 0; // 收到事件说明监听正常，退避计数归零

        // —— 首行：内部操作抑制表检查点（本程序自身引起的改名直接丢弃，防死循环）——
        // 注：RenamedEventArgs 的新路径属性是 FullPath，旧路径是 OldFullPath
        if (_internalOps.TryConsume(e.OldFullPath) || _internalOps.TryConsume(e.FullPath))
        {
            Log.Info($"[判定] 忽略（内部操作标记） | 旧: {e.OldFullPath} | 新: {e.FullPath}");
            return;
        }

        // —— 按开销升序早退判定：扩展名白名单 → 排除目录前缀 → 排除文件模式 ——
        var (shouldProcess, reason) = PathRules.Judge(e.OldFullPath, e.FullPath);
        if (!shouldProcess)
        {
            Log.Info($"[判定] 忽略 | 旧: {e.OldFullPath} | 新: {e.FullPath} | 原因: {reason}");
            return;
        }

        Log.Info($"[判定] 通过 | 旧: {e.OldFullPath} | 新: {e.FullPath} | {reason}");
        _debounce.Schedule(e.OldFullPath, e.FullPath);
    }

    /// <summary>防抖到期后的处理入口。里程碑 1 仅输出日志；里程碑 2 在此接入备份 + 转换流程。</summary>
    private void OnDebounceDue(string oldPath, string newPath)
    {
        Log.Info($"[处理] 开始处理（防抖合并后） | 旧: {oldPath} | 新: {newPath}");
    }

    /// <summary>Error 回调：记录溢出时间窗口并按指数退避重建全部 watcher。</summary>
    private void OnError(object sender, ErrorEventArgs e)
    {
        var now = DateTime.UtcNow;
        var ex = e.GetException();
        if (ex is InternalBufferOverflowException)
        {
            // 记录丢失事件的时间窗口（上次收到事件 → 溢出发生）
            Log.Warn($"[监听] 缓冲区溢出，事件丢失时间窗口：{_lastEventUtc:yyyy-MM-dd HH:mm:ss} ~ {now:yyyy-MM-dd HH:mm:ss}（UTC） | {ex.Message}");
        }
        else
        {
            Log.Error($"[监听] 发生错误：{ex.Message}");
        }

        // 指数退避重建：1s / 5s / 30s
        var delay = RebuildDelays[Math.Min(_errorStreak, RebuildDelays.Length - 1)];
        _errorStreak++;
        Log.Info($"[监听] {delay.TotalSeconds:0} 秒后重建监听器…");
        _ = RebuildAfterAsync(delay);
    }

    /// <summary>延迟重建全部 watcher（不在事件线程上阻塞）。</summary>
    private async Task RebuildAfterAsync(TimeSpan delay)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        lock (_sync)
        {
            if (_disposed || _paused) return;
            DisposeAllWatchers();
            StartAllWatchers();
            Log.Info("[监听] 监听器已重建");
        }
    }

    /// <summary>释放全部资源，停止监听。</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            DisposeAllWatchers();
        }
    }
}