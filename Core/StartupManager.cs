using System.Diagnostics;

namespace RenamePro.Core;

/// <summary>
/// 开机自启动管理：通过 Windows 任务计划程序（schtasks.exe 子进程）注册“登录时自动运行”。
/// 只依据进程退出码判断结果，不解析本地化输出，中英文系统通用。
/// 注册/注销通常需要管理员权限：先尝试普通权限，失败再触发 UAC 提权。
/// </summary>
public static class StartupManager
{
    /// <summary>任务计划程序中的任务名。</summary>
    private const string TaskName = "RenamePro";

    /// <summary>
    /// 查询开机自启动任务是否已注册（无需提权）。
    /// </summary>
    /// <returns>已注册返回 true</returns>
    public static bool IsRegistered() =>
        RunSchtasks($"/Query /TN \"{TaskName}\"", elevate: false) == 0;

    /// <summary>
    /// 注册开机自启动（登录时运行，/F 幂等覆盖已有任务）。可能弹一次 UAC 提权。
    /// </summary>
    /// <returns>注册成功返回 true</returns>
    public static bool Register()
    {
        var exePath = Environment.ProcessPath ?? Application.ExecutablePath;
        // /TR 的值需要双层引号：外层引号属于 schtasks 参数，内层引号保证带空格/中文的路径被正确解析
        var args = $"/Create /TN \"{TaskName}\" /TR \"\\\"{exePath}\\\"\" /SC ONLOGON /RL LIMITED /F";
        if (RunSchtasks(args, elevate: false) == 0) return true;
        Log.Info("普通权限注册失败，尝试 UAC 提权注册开机自启动…");
        return RunSchtasks(args, elevate: true) == 0;
    }

    /// <summary>
    /// 注销开机自启动任务。可能弹一次 UAC 提权。
    /// </summary>
    /// <returns>注销成功返回 true</returns>
    public static bool Unregister()
    {
        var args = $"/Delete /TN \"{TaskName}\" /F";
        if (RunSchtasks(args, elevate: false) == 0) return true;
        return RunSchtasks(args, elevate: true) == 0;
    }

    /// <summary>
    /// 执行 schtasks.exe 并返回退出码（0=成功）；异常返回 -1。
    /// </summary>
    /// <param name="arguments">schtasks 命令行参数</param>
    /// <param name="elevate">true 表示以 runas 触发 UAC 提权执行</param>
    private static int RunSchtasks(string arguments, bool elevate)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true, // 绝不闪黑色控制台窗口
            };
            if (elevate)
            {
                // 提权走 runas（触发 UAC）；此时无法重定向输出，只看退出码
                startInfo.UseShellExecute = true;
                startInfo.Verb = "runas";
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            }
            using var process = Process.Start(startInfo);
            if (process == null) return -1;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            // 典型场景：用户在 UAC 弹窗点了“否”
            Log.Warn($"schtasks 执行失败（{ex.Message}）：{arguments}");
            return -1;
        }
    }
}