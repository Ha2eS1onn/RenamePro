using RenamePro.Core;
using RenamePro.Watching;

namespace RenamePro.Tray;

/// <summary>
/// 托盘应用上下文：以托盘图标常驻后台，提供“暂停监听 / 开机自启动 / 退出”菜单，不显示任何主窗口。
/// </summary>
public sealed class TrayAppContext : ApplicationContext
{
    /// <summary>托盘图标。</summary>
    private readonly NotifyIcon _trayIcon;

    /// <summary>“暂停监听 / 继续监听”菜单项。</summary>
    private readonly ToolStripMenuItem _pauseMenuItem;

    /// <summary>“开机自启动”勾选菜单项。</summary>
    private readonly ToolStripMenuItem _startupMenuItem;

    /// <summary>内部操作抑制表（传给监听器做首行检查点）。</summary>
    private readonly InternalOpsSet _internalOps = new();

    /// <summary>文件改名监听器。</summary>
    private readonly RenameWatcher _watcher;

    /// <summary>图标资源流（Icon 不复制流数据，需保持到程序退出）。</summary>
    private readonly Stream _iconStream;

    /// <summary>托盘图标对象（退出时释放）。</summary>
    private readonly Icon _icon;

    /// <summary>是否已执行过退出流程（防止重复退出）。</summary>
    private bool _exited;

    /// <summary>
    /// 创建托盘界面并启动监听、注册开机自启动。
    /// </summary>
    public TrayAppContext()
    {
        // 图标来自嵌入资源（exe 自身图标也用同一文件）
        var stream = typeof(TrayAppContext).Assembly.GetManifestResourceStream("RenamePro.Assets.app.ico");
        if (stream != null)
        {
            _iconStream = stream;
            _icon = new Icon(stream);
        }
        else
        {
            // 兜底：嵌入资源缺失时用系统图标，保证托盘可见
            _iconStream = Stream.Null;
            _icon = SystemIcons.Application;
        }

        _pauseMenuItem = new ToolStripMenuItem("暂停监听");
        _pauseMenuItem.Click += (_, _) => TogglePause();

        _startupMenuItem = new ToolStripMenuItem("开机自启动") { CheckOnClick = true };
        _startupMenuItem.Click += (_, _) => ToggleStartup();

        var exitMenuItem = new ToolStripMenuItem("退出");
        exitMenuItem.Click += (_, _) => ExitApplication();

        var menu = new ContextMenuStrip();
        // 菜单打开时刷新勾选状态（注册可能刚在后台完成）
        menu.Opening += (_, _) => _startupMenuItem.Checked = StartupManager.IsRegistered();
        menu.Items.Add(_pauseMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startupMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitMenuItem);

        _trayIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "RenamePro - 改名格式转换助手",
            ContextMenuStrip = menu,
            Visible = true,
        };

        Log.Info("程序已启动，托盘常驻");
        _watcher = new RenameWatcher(_internalOps);

        // 开机自启动：未注册则自动注册（幂等，可能弹一次 UAC，失败不影响监听）
        EnsureStartupRegistration();
    }

    /// <summary>切换“暂停监听 / 继续监听”。</summary>
    private void TogglePause()
    {
        if (_pauseMenuItem.Checked)
        {
            _watcher.Resume();
            _pauseMenuItem.Checked = false;
            _pauseMenuItem.Text = "暂停监听";
        }
        else
        {
            _watcher.Pause();
            _pauseMenuItem.Checked = true;
            _pauseMenuItem.Text = "继续监听";
        }
    }

    /// <summary>切换开机自启动（后台执行，可能弹 UAC）。</summary>
    private void ToggleStartup()
    {
        var enable = _startupMenuItem.Checked;
        Task.Run(() =>
        {
            var ok = enable ? StartupManager.Register() : StartupManager.Unregister();
            Log.Info(ok
                ? (enable ? "已注册开机自启动（任务计划程序）" : "已注销开机自启动（任务计划程序）")
                : "切换开机自启动失败（用户取消 UAC 或权限不足）");
        });
    }

    /// <summary>启动时自动注册开机自启动（已注册则跳过，避免每次启动都弹 UAC）。</summary>
    private void EnsureStartupRegistration()
    {
        Task.Run(() =>
        {
            if (StartupManager.IsRegistered())
            {
                Log.Info("开机自启动任务已存在，跳过注册");
                return;
            }
            if (StartupManager.Register())
                Log.Info("已自动注册开机自启动（任务计划程序）");
            else
                Log.Warn("自动注册开机自启动失败，可在托盘菜单“开机自启动”重试");
        });
    }

    /// <summary>退出程序：停监听、释放托盘图标。</summary>
    private void ExitApplication()
    {
        if (_exited) return;
        _exited = true;
        Log.Info("程序退出");
        _watcher.Dispose();
        _internalOps.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _icon.Dispose();
        _iconStream.Dispose();
        ExitThread();
    }
}