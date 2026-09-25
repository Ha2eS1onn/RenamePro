namespace RenamePro.Progress;

/// <summary>
/// 专用 STA 消息线程调度器：Shell 进度对话框（COM）要求 STA + 消息循环，
/// 因此单独开一个后台 STA 线程跑独立消息循环，线程上创建隐藏窗口作为封送入口；
/// 对话框的创建、进度更新、销毁一律通过 <see cref="Invoke{T}"/>/<see cref="BeginInvoke"/> 投递到该线程执行，
/// 禁止在主线程或线程池线程直接调用 Shell COM。
/// </summary>
public sealed class StaDispatcher : IDisposable
{
    /// <summary>专用 STA 线程。</summary>
    private readonly Thread _thread;

    /// <summary>就绪信号：等 STA 线程上的窗口创建完成。</summary>
    private readonly ManualResetEventSlim _ready = new(false);

    /// <summary>封送用隐藏窗口（从不显示，仅用于 Invoke/BeginInvoke）。</summary>
    private Control? _marshalWindow;

    /// <summary>是否已释放。</summary>
    private volatile bool _disposed;

    /// <summary>构造函数：启动 STA 线程并等待就绪。</summary>
    public StaDispatcher()
    {
        _thread = new Thread(ThreadProc)
        {
            IsBackground = true, // 不阻塞进程退出
            Name = "RenamePro.ShellComSta"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(); // 等待 STA 线程上的消息循环就绪
    }

    /// <summary>STA 线程主体：初始化 OLE + 创建隐藏窗口 + 跑独立消息循环。</summary>
    private void ThreadProc()
    {
        // Shell 对话框要求该 STA 线程已初始化 OLE（WinForms 主线程会隐式初始化，专用线程必须显式调用）
        Application.OleRequired();
        // 隐藏窗口（不显示任何 UI）：Control 句柄即封送入口
        _marshalWindow = new Control();
        _marshalWindow.CreateControl();
        _ = _marshalWindow.Handle; // 强制创建句柄
        _ready.Set();
        // 本线程独立消息循环（Shell COM 对话框需要消息泵）
        Application.Run(new ApplicationContext());
    }

    /// <summary>同步封送到 STA 线程执行并取回结果。</summary>
    /// <param name="func">要执行的操作</param>
    /// <typeparam name="T">返回值类型</typeparam>
    public T Invoke<T>(Func<T> func)
    {
        var window = _marshalWindow ?? throw new ObjectDisposedException(nameof(StaDispatcher));
        return window.InvokeRequired ? (T)window.Invoke(func) : func();
    }

    /// <summary>异步封送到 STA 线程执行（不等待）。</summary>
    /// <param name="action">要执行的操作</param>
    public void BeginInvoke(Action action)
    {
        var window = _marshalWindow;
        if (window == null || _disposed) return;
        try
        {
            window.BeginInvoke(action);
        }
        catch (ObjectDisposedException)
        {
            // 线程已退出，忽略
        }
    }

    /// <summary>结束 STA 线程消息循环并释放资源。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var window = _marshalWindow;
        if (window != null)
        {
            try
            {
                // 在 STA 线程上收尾：销毁窗口并退出消息循环
                window.BeginInvoke(new Action(() =>
                {
                    window.Dispose();
                    Application.ExitThread();
                }));
            }
            catch (ObjectDisposedException)
            {
                // 已退出
            }
        }
        _ready.Dispose();
    }
}