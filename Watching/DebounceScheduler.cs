using System.Collections.Concurrent;

namespace RenamePro.Watching;

/// <summary>
/// 防抖合并调度器：同一路径 300ms 内的多次改名只处理最后一次。
/// 实现：ConcurrentDictionary&lt;string, CancellationTokenSource&gt; + 可取消延时（Task.Delay 作可取消 Timer），
/// 既规避资源管理器改名瞬间的句柄占用，又把连续改名合并为一次处理。
/// </summary>
public sealed class DebounceScheduler
{
    /// <summary>防抖窗口：300 毫秒。</summary>
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(300);

    /// <summary>挂起表：键为新路径（大小写不敏感），值为可取消的延时控制。</summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>防抖到期后的回调（在线程池线程执行）。</summary>
    private readonly Action<string, string> _onDue;

    /// <summary>
    /// 创建调度器。
    /// </summary>
    /// <param name="onDue">到期回调，参数为（旧路径, 新路径）</param>
    public DebounceScheduler(Action<string, string> onDue) => _onDue = onDue;

    /// <summary>
    /// 提交一次改名：同路径 300ms 内的旧任务被取消（只留最后一次）；
    /// 若旧路径上还有挂起任务（链式改名：a.png→a.jpeg 紧跟 a.jpg→a.png）也一并取消。
    /// </summary>
    /// <param name="oldPath">改名前路径</param>
    /// <param name="newPath">改名后路径</param>
    public void Schedule(string oldPath, string newPath)
    {
        // 链式改名：旧路径上的挂起任务目标文件已不在原名，取消它（只处理最后一次）
        if (_pending.TryRemove(oldPath, out var stale)) stale.Cancel();

        // 同路径 300ms 内再次改名：取消旧任务
        if (_pending.TryRemove(newPath, out var previous)) previous.Cancel();

        // 登记新任务
        var cts = new CancellationTokenSource();
        _pending[newPath] = cts;
        _ = RunAfterDelayAsync(oldPath, newPath, cts);
    }

    /// <summary>延时 300ms 后执行处理；被取消则静默丢弃。</summary>
    private async Task RunAfterDelayAsync(string oldPath, string newPath, CancellationTokenSource cts)
    {
        try
        {
            // 可取消 Timer：后续改名会 Cancel 本延时
            await Task.Delay(DebounceWindow, cts.Token).ConfigureAwait(false);

            // 到期：从挂起表摘除自己（仅当仍是自己，避免误删更新的任务）
            if (_pending.TryGetValue(newPath, out var current) && ReferenceEquals(current, cts))
                _pending.TryRemove(newPath, out _);

            _onDue(oldPath, newPath);
        }
        catch (OperationCanceledException)
        {
            // 被更新的改名取消——静默丢弃（只处理最后一次）
        }
        finally
        {
            cts.Dispose();
        }
    }
}