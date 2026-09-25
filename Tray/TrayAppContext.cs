using RenamePro.Conversion;
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

    /// <summary>转换主流程（备份 + 转换 + 替换）。</summary>
    private readonly ConversionPipeline _pipeline;

    /// <summary>图标资源流（Icon 不复制流数据，需保持到程序退出）。</summary>
    private readonly Stream _iconStream;

    /// <summary>托盘图标对象（退出时释放）。</summary>
    private readonly Icon _icon;

    /// <summary>Shell 广播消息监听窗口（TaskbarCreated 重建图标 / 重复启动气泡提示）。</summary>
    private readonly ShellMessageWindow _shellMessageWindow;

    /// <summary>闪烁帧图标（系统共享图标，无需释放）：与正常图标差异明显，保证闪烁肉眼可见。</summary>
    private readonly Icon _flashIcon = SystemIcons.Application;

    /// <summary>托盘图标闪烁定时器（UI 线程，300ms 一拍；注意全限定名，避免与 System.Threading.Timer 混淆）。</summary>
    private readonly System.Windows.Forms.Timer _flashTimer;

    /// <summary>剩余闪烁节拍数，归零即停止闪烁并恢复正常图标。</summary>
    private int _flashTicks;

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

        // 隐藏消息窗口：TaskbarCreated 广播 → 重建托盘图标；“已在运行”广播 → 气泡提示（均在 UI 线程执行）
        _shellMessageWindow = new ShellMessageWindow(ReassertTrayIcon, ShowAlreadyRunningTip);

        // 图标闪烁定时器：重复启动提示用，仅在提示期间启用
        _flashTimer = new System.Windows.Forms.Timer { Interval = 300 };
        _flashTimer.Tick += (_, _) => OnFlashTick();

        Log.Info("程序已启动，托盘常驻");
        _pipeline = new ConversionPipeline(_internalOps);
        _watcher = new RenameWatcher(_internalOps, _pipeline.Submit);

        // 开机自启动：未注册则自动注册（幂等，可能弹一次 UAC，失败不影响监听）
        EnsureStartupRegistration();
    }

    /// <summary>
    /// 强制重新注册托盘图标（收到 TaskbarCreated 广播时调用）：
    /// 覆盖“登录早期通知区未就绪导致注册静默失败”与“Explorer 重启后图标丢失”两种场景。
    /// </summary>
    private void ReassertTrayIcon()
    {
        if (_exited) return;
        // 先卸载再装载，强制 Shell_NotifyIcon 重新注册
        _trayIcon.Visible = false;
        _trayIcon.Visible = true;
        Log.Info("[托盘] 通知区就绪（或 Explorer 重启），托盘图标已重建");
    }

    /// <summary>
    /// 显示“已在后台运行”提示：气泡提示（系统通知允许时弹出）+ 托盘图标闪烁（必定可见，不依赖系统通知设置）。
    /// 收到重复启动 exe 的广播时调用。
    /// </summary>
    private void ShowAlreadyRunningTip()
    {
        if (_exited) return;
        // 气泡提示：受系统通知设置/专注助手/远程桌面影响可能被静默收起，故叠加图标闪烁
        _trayIcon.ShowBalloonTip(3000, "RenamePro", "程序已在后台运行（系统托盘）", ToolTipIcon.Info);
        // 托盘图标闪烁约 3 秒（8 拍 × 300ms）
        _flashTicks = 8;
        _flashTimer.Start();
        Log.Info("[托盘] 收到重复启动信号，气泡提示 + 托盘图标闪烁");
    }

    /// <summary>闪烁节拍：交替切换图标形成闪烁，节拍归零后恢复正常图标。</summary>
    private void OnFlashTick()
    {
        if (_exited) return;
        _flashTicks--;
        if (_flashTicks <= 0)
        {
            // 结束闪烁并恢复正常图标
            _flashTimer.Stop();
            _trayIcon.Icon = _icon;
            return;
        }
        // 与当前图标取反：正常 ↔ 闪烁帧，每拍交替
        _trayIcon.Icon = ReferenceEquals(_trayIcon.Icon, _icon) ? _flashIcon : _icon;
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
        _pipeline.Dispose();
        _internalOps.Dispose();
        _shellMessageWindow.Dispose();
        _flashTimer.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _icon.Dispose();
        _iconStream.Dispose();
        ExitThread();
    }
}