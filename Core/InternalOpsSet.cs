using System.Collections.Concurrent;

namespace RenamePro.Core;

/// <summary>
/// 内部操作抑制表（防死循环的关键）：
/// 本程序自身引起的一切路径变更（自动回滚时副本 Move 回原名、File.Replace 前的保险登记等）
/// 必须在操作前登记；watcher 回调首行检查，命中即丢弃该事件，
/// 否则开启 autoRollbackOnFailure 后刚恢复的文件会被立即再次转换。
/// 结构为 ConcurrentDictionary&lt;string, DateTime&gt;，标记带 5 秒 TTL 自动过期。
/// </summary>
public sealed class InternalOpsSet : IDisposable
{
    /// <summary>标记有效期：5 秒后自动过期。</summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);

    /// <summary>标记表：键为路径（Windows 大小写不敏感），值为登记时间（UTC）。</summary>
    private readonly ConcurrentDictionary<string, DateTime> _marks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>周期清扫定时器，防止事件丢失导致标记泄漏。</summary>
    private readonly System.Threading.Timer _sweepTimer;

    /// <summary>创建抑制表并启动周期清扫。</summary>
    public InternalOpsSet()
    {
        // 每 5 秒清扫一次过期标记（与 TTL 同周期）
        _sweepTimer = new System.Threading.Timer(_ => Sweep(), null, Ttl, Ttl);
    }

    /// <summary>
    /// 登记一个即将发生的内部路径操作。必须在操作“之前”调用。
    /// </summary>
    /// <param name="path">将要发生变化的路径（被 Move/Replace 的路径）</param>
    public void Register(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        _marks[path] = DateTime.UtcNow;
    }

    /// <summary>
    /// 检查并消费标记：命中未过期的标记则移除它并返回 true（对应事件应被丢弃）；否则返回 false。
    /// </summary>
    /// <param name="path">事件中的路径</param>
    /// <returns>true 表示这是本程序自身引起的改名，应忽略</returns>
    public bool TryConsume(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (!_marks.TryRemove(path, out var registeredUtc)) return false;
        // 超过 TTL 的标记视为过期，不再抑制
        return DateTime.UtcNow - registeredUtc <= Ttl;
    }

    /// <summary>清除所有过期标记。</summary>
    public void Sweep()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _marks)
        {
            if (now - kv.Value > Ttl) _marks.TryRemove(kv.Key, out _);
        }
    }

    /// <summary>释放清扫定时器。</summary>
    public void Dispose() => _sweepTimer.Dispose();
}