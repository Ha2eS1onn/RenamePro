namespace RenamePro.Conversion;

/// <summary>任务通道：分级队列的三级通道。</summary>
public enum ConversionLane
{
    /// <summary>快速通道（并发 2）：FFmpeg 流拷贝任务、小于 2MB 的图片任务。</summary>
    Fast,

    /// <summary>图片通道（并发 2）：Magick.NET 位图转换。</summary>
    Image,

    /// <summary>音视频通道（并发 1，全局串行）：需要重编码的 FFmpeg 任务。</summary>
    AudioVideo
}

/// <summary>
/// 分级调度队列：三条通道各自限流（2 / 2 / 1），任务体（备份 + 转换 + 替换）在通道槽内执行。
/// 取消粒度：单任务令牌取消“当前任务”，<see cref="CancelAll"/> 取消全部队列；
/// 两者都不影响队列继续处理剩余任务。
/// </summary>
public sealed class ConversionScheduler : IDisposable
{
    /// <summary>快速通道并发度 2。</summary>
    private readonly SemaphoreSlim _fastLane = new(2);

    /// <summary>图片通道并发度 2。</summary>
    private readonly SemaphoreSlim _imageLane = new(2);

    /// <summary>音视频通道并发度 1（重编码单任务即可打满 CPU）。</summary>
    private readonly SemaphoreSlim _avLane = new(1);

    /// <summary>取消全部队列用的同步锁。</summary>
    private readonly object _cancelLock = new();

    /// <summary>“取消全部”令牌源；CancelAll 后换新，保证后续任务仍可继续。</summary>
    private CancellationTokenSource _cancelAll = new();

    /// <summary>是否已释放。</summary>
    private bool _disposed;

    /// <summary>
    /// 把任务体提交到指定通道执行（受该通道并发度约束）。
    /// </summary>
    /// <param name="lane">通道</param>
    /// <param name="body">任务体（备份 + 转换 + 替换），收到取消令牌</param>
    /// <param name="taskToken">单任务取消令牌（取消当前任务用）</param>
    /// <returns>可等待的 Task</returns>
    public Task RunAsync(ConversionLane lane, Func<CancellationToken, Task> body, CancellationToken taskToken)
    {
        // 单任务令牌 + 全局取消令牌合并
        CancellationTokenSource linked;
        lock (_cancelLock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ConversionScheduler));
            linked = CancellationTokenSource.CreateLinkedTokenSource(taskToken, _cancelAll.Token);
        }

        return Task.Run(async () =>
        {
            var semaphore = lane switch
            {
                ConversionLane.Fast => _fastLane,
                ConversionLane.Image => _imageLane,
                _ => _avLane
            };
            try
            {
                await semaphore.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    await body(linked.Token).ConfigureAwait(false);
                }
                finally
                {
                    semaphore.Release();
                }
            }
            catch (OperationCanceledException)
            {
                // 取消由任务体自行记 CANCELLED；这里兜底，保证槽位已释放
            }
            finally
            {
                linked.Dispose();
            }
        });
    }

    /// <summary>
    /// 取消全部队列任务（进行中与未开始的都收到取消信号）；
    /// 取消后队列继续可接收并处理新任务。
    /// </summary>
    public void CancelAll()
    {
        lock (_cancelLock)
        {
            _cancelAll.Cancel();
            _cancelAll.Dispose();
            _cancelAll = new CancellationTokenSource();
        }
    }

    /// <summary>释放三条通道与取消令牌源。</summary>
    public void Dispose()
    {
        lock (_cancelLock)
        {
            if (_disposed) return;
            _disposed = true;
            _cancelAll.Cancel();
            _cancelAll.Dispose();
        }
        _fastLane.Dispose();
        _imageLane.Dispose();
        _avLane.Dispose();
    }
}