using System.Diagnostics;
using System.Text;

namespace RenamePro.Core;

/// <summary>
/// 简单的线程安全日志工具：把日志追加写入程序同目录下的 log.txt。
/// 程序为 WinExe（无控制台窗口），因此日志文件就是事件捕获与判定结果的验证手段。
/// </summary>
public static class Log
{
    /// <summary>日志文件完整路径（程序同目录 log.txt）。</summary>
    private static readonly string LogFilePath = Path.Combine(AppContext.BaseDirectory, "log.txt");

    /// <summary>写锁：watcher 线程与线程池线程并发写入时保证不交错错乱。</summary>
    private static readonly object WriteLock = new();

    /// <summary>写入一条普通信息日志。</summary>
    /// <param name="message">日志内容</param>
    public static void Info(string message) => Write("INFO", message);

    /// <summary>写入一条警告日志。</summary>
    /// <param name="message">日志内容</param>
    public static void Warn(string message) => Write("WARN", message);

    /// <summary>写入一条错误日志。</summary>
    /// <param name="message">日志内容</param>
    public static void Error(string message) => Write("ERROR", message);

    /// <summary>格式化时间戳并追加一行日志，同时输出到调试器（便于开发期观察）。</summary>
    /// <param name="level">日志级别</param>
    /// <param name="message">日志内容</param>
    private static void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";
        lock (WriteLock)
        {
            try
            {
                File.AppendAllText(LogFilePath, line, Encoding.UTF8);
            }
            catch
            {
                // 日志写入失败绝不影响主流程，静默忽略
            }
        }
        Debug.WriteLine(line);
    }
}