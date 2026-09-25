using System.Security;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using RenamePro.Core;
using RenamePro.Interop;

namespace RenamePro.Progress;

/// <summary>
/// 通知服务：发送 Windows Toast 汇总（非打包应用需 AUMID + 开始菜单快捷方式）。
/// 任一步失败都回退到托盘图标闪烁，保证有可见反馈，并写日志。
/// 里程碑 3 用于 COM 降级后的完成汇总；里程碑 4 的 enableToast 直接复用。
/// </summary>
public static class ToastService
{
    /// <summary>应用用户模型 ID（Toast 归属标识，须与开始菜单快捷方式一致）。</summary>
    private const string AppUserModelId = "RenamePro.Toast";

    /// <summary>开始菜单快捷方式文件名。</summary>
    private const string ShortcutFileName = "RenamePro.lnk";

    /// <summary>是否已初始化（幂等）。</summary>
    private static bool _initialized;

    /// <summary>初始化是否成功。</summary>
    private static bool _available;

    /// <summary>初始化锁。</summary>
    private static readonly object Sync = new();

    /// <summary>
    /// 初始化：注册 AUMID + 确保开始菜单快捷方式存在（Toast 弹出的必要条件）。
    /// 幂等，失败不抛异常。
    /// </summary>
    public static void Initialize()
    {
        lock (Sync)
        {
            if (_initialized) return;
            _initialized = true;
            try
            {
                ShellItemHelper.SetProcessAppUserModelId(AppUserModelId);
                var shortcutPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs), ShortcutFileName);
                if (!File.Exists(shortcutPath)) CreateShortcut(shortcutPath);
                ShellItemHelper.SetShortcutAppUserModelId(shortcutPath, AppUserModelId);
                _available = true;
            }
            catch (Exception ex)
            {
                _available = false;
                Log.Warn($"Toast 初始化失败（将回退托盘闪烁）：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// 显示一条 Toast 汇总。
    /// </summary>
    /// <param name="title">标题</param>
    /// <param name="lines">正文行（可为多行，最多取前 4 行）</param>
    /// <returns>成功返回 true；失败返回 false（调用方应回退托盘闪烁）</returns>
    public static bool ShowSummary(string title, IReadOnlyList<string> lines)
    {
        Initialize();
        if (!_available) return false;
        try
        {
            var document = new XmlDocument();
            var text = new System.Text.StringBuilder();
            text.Append("<toast><visual><binding template=\"ToastGeneric\"><text>")
                .Append(Escape(title)).Append("</text>");
            foreach (var line in lines.Take(4))
            {
                text.Append("<text>").Append(Escape(line)).Append("</text>");
            }
            text.Append("</binding></visual></toast>");
            document.LoadXml(text.ToString());

            var toast = new ToastNotification(document);
            ToastNotificationManager.CreateToastNotifier(AppUserModelId).Show(toast);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Toast 显示失败（将回退托盘闪烁）：{ex.Message}");
            return false;
        }
    }

    /// <summary>通过 WScript.Shell 创建指向本程序 exe 的开始菜单快捷方式。</summary>
    /// <param name="shortcutPath">.lnk 完整路径</param>
    private static void CreateShortcut(string shortcutPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
                        ?? throw new InvalidOperationException("无法创建 WScript.Shell（系统组件缺失）");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = Environment.ProcessPath ?? Application.ExecutablePath;
        shortcut.Description = "RenamePro - 改名格式转换助手";
        shortcut.Save();
    }

    /// <summary>XML 转义（Toast 模板为 XML）。</summary>
    private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;
}