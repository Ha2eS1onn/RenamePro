using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

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
    /// 读取自启动任务当前指向的可执行文件路径；未注册或解析失败返回 null。
    /// </summary>
    /// <returns>任务中的 exe 完整路径或 null</returns>
    public static string? GetRegisteredCommand()
    {
        var xml = RunSchtasksCapture($"/Query /TN \"{TaskName}\" /XML");
        if (string.IsNullOrEmpty(xml)) return null;
        // schtasks 的 XML 中 <Command> 即任务要运行的程序；用轻量正则提取即可（不做整树解析，避免编码声明坑）
        var match = Regex.Match(xml, "<Command>(.*?)</Command>", RegexOptions.Singleline);
        if (!match.Success) return null;
        var command = match.Groups[1].Value.Trim().Trim('"');
        return string.IsNullOrEmpty(command) ? null : command;
    }

    /// <summary>
    /// 确保自启动任务已注册且指向当前程序：
    /// 未注册 → 注册；已注册但路径过期（构建目录变更/程序迁移）→ 自动重新注册（幂等覆盖）。
    /// 可能弹一次 UAC 提权。
    /// </summary>
    public static void EnsureRegistered()
    {
        var currentExe = Environment.ProcessPath ?? Application.ExecutablePath;
        var registered = GetRegisteredCommand();
        if (registered == null)
        {
            Log.Info(Register() ? "已自动注册开机自启动（任务计划程序）" : "自动注册开机自启动失败，可在托盘菜单“开机自启动”重试");
            return;
        }
        if (string.Equals(registered, currentExe, StringComparison.OrdinalIgnoreCase))
        {
            Log.Info("开机自启动任务已存在且指向当前程序，跳过注册");
            return;
        }

        // 任务指向的是旧路径（例如构建输出目录变更后）——自动纠正，避免登录自启运行僵尸程序
        Log.Info($"开机自启动任务指向已过期：{registered}，正在更新为当前程序：{currentExe}");
        Log.Info(Register() ? "已更新开机自启动任务路径" : "更新开机自启动任务路径失败，可在托盘菜单“开机自启动”重试");
    }

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

    /// <summary>
    /// 执行 schtasks.exe 并捕获标准输出（用于读取任务 XML）；失败返回空串。
    /// </summary>
    /// <param name="arguments">schtasks 命令行参数</param>
    private static string RunSchtasksCapture(string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(startInfo);
            if (process == null) return string.Empty;
            // 直接读原始字节：schtasks /XML 可能输出 UTF-16，交给解码函数按 BOM 判定
            using var buffer = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(buffer);
            process.WaitForExit();
            return process.ExitCode == 0 ? DecodeShellOutput(buffer.ToArray()) : string.Empty;
        }
        catch (Exception ex)
        {
            Log.Warn($"schtasks 查询失败（{ex.Message}）：{arguments}");
            return string.Empty;
        }
    }

    /// <summary>
    /// 解码 schtasks 输出：按 BOM 判定 UTF-16/UTF-8，无 BOM 时用系统默认代码页。
    /// </summary>
    /// <param name="bytes">原始输出字节</param>
    private static string DecodeShellOutput(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        return Encoding.Default.GetString(bytes);
    }
}