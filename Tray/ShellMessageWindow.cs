using System.Runtime.InteropServices;
using RenamePro.Core;

namespace RenamePro.Tray;

/// <summary>
/// Shell 广播消息监听窗口：一个隐藏的顶层窗口（不显示任何 UI，不是“主窗口”），用于接收系统广播消息。
/// 监听两类消息：
/// 1) TaskbarCreated —— Explorer 通知区刚就绪或 Explorer 重启，需要重建托盘图标；
/// 2) RenamePro_AlreadyRunning —— 有重复启动的 exe，弹气泡提示“已在后台运行”。
/// 注意：消息只窗口（HWND_MESSAGE）收不到广播，必须创建隐藏的顶层窗口。
/// </summary>
public sealed class ShellMessageWindow : NativeWindow, IDisposable
{
    /// <summary>TaskbarCreated 广播消息号（运行时注册，非固定值）。</summary>
    private static readonly int MsgTaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    /// <summary>收到 TaskbarCreated 时的回调（在 UI 线程执行）。</summary>
    private readonly Action _onTaskbarCreated;

    /// <summary>收到“已在运行”广播时的回调（在 UI 线程执行）。</summary>
    private readonly Action _onAlreadyRunning;

    /// <summary>
    /// 创建隐藏顶层窗口并开始监听广播。
    /// </summary>
    /// <param name="onTaskbarCreated">收到 TaskbarCreated 广播时的回调</param>
    /// <param name="onAlreadyRunning">收到“已在运行”广播时的回调</param>
    public ShellMessageWindow(Action onTaskbarCreated, Action onAlreadyRunning)
    {
        _onTaskbarCreated = onTaskbarCreated;
        _onAlreadyRunning = onAlreadyRunning;
        // CreateParams 全空 = 隐藏的顶层窗口，仅接收消息，不显示任何 UI
        CreateHandle(new CreateParams());
    }

    /// <summary>窗口过程：拦截关心的广播消息并触发对应回调。</summary>
    /// <param name="m">Windows 消息</param>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == MsgTaskbarCreated)
        {
            _onTaskbarCreated();
        }
        else if (m.Msg == SingleInstance.AlreadyRunningMessage)
        {
            _onAlreadyRunning();
        }
        base.WndProc(ref m);
    }

    /// <summary>销毁隐藏窗口。</summary>
    public void Dispose() => DestroyHandle();

    /// <summary>注册一个 Windows 广播消息（P/Invoke）。</summary>
    /// <param name="message">消息名称</param>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string message);
}