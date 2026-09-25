namespace RenamePro.Core;

/// <summary>
/// 文件操作重试辅助：遇到 IOException（典型为文件被占用）按 500ms 间隔最多重试 3 次，
/// 仍失败则抛出原异常，由上层把任务标记为失败。
/// </summary>
public static class IoRetry
{
    /// <summary>重试间隔：500 毫秒。</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>最大重试次数。</summary>
    private const int MaxRetries = 3;

    /// <summary>
    /// 执行无返回值的文件操作；IOException 时重试，仍失败抛出原异常。
    /// </summary>
    /// <param name="opName">操作名（用于日志）</param>
    /// <param name="action">文件操作</param>
    public static void Run(string opName, Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException ex)
            {
                if (attempt > MaxRetries) throw; // 仍失败则任务失败
                Log.Warn($"[重试] {opName} 第 {attempt} 次失败（{ex.Message}），500ms 后重试（最多 {MaxRetries} 次）");
                Thread.Sleep(RetryInterval);
            }
        }
    }

    /// <summary>
    /// 执行带返回值的文件操作；IOException 时重试，仍失败抛出原异常。
    /// 注意：必须独立实现循环（不能复用 Run(Action)），否则值型 lambda 会匹配到 Func 重载造成自递归。
    /// </summary>
    /// <param name="opName">操作名（用于日志）</param>
    /// <param name="func">文件操作</param>
    /// <returns>操作结果</returns>
    public static T Run<T>(string opName, Func<T> func)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return func();
            }
            catch (IOException ex)
            {
                if (attempt > MaxRetries) throw; // 仍失败则任务失败
                Log.Warn($"[重试] {opName} 第 {attempt} 次失败（{ex.Message}），500ms 后重试（最多 {MaxRetries} 次）");
                Thread.Sleep(RetryInterval);
            }
        }
    }
}