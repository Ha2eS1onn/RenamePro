using System.Runtime.InteropServices;

namespace RenamePro.Core;

/// <summary>
/// 单实例保护：通过命名互斥体保证程序只运行一个实例。
/// 重复启动的进程会向已有实例广播“已在运行”消息（托盘弹气泡提示）后立即退出，
/// 绝不产生第二个托盘图标。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>互斥体名称（Local\ 前缀 = 会话级，同一用户的不同登录会话可各自运行）。</summary>
    private const string MutexName = @"Local\RenamePro_SingleInstance";

    /// <summary>“已在运行”广播消息号（与 Tray\ShellMessageWindow 共用，运行时注册）。</summary>
    public static readonly int AlreadyRunningMessage = RegisterWindowMessage("RenamePro_AlreadyRunning");

    /// <summary>本实例持有的互斥体（进程生命周期内持有）；创建失败放行时为 null。</summary>
    private readonly Mutex? _mutex;

    /// <summary>私有构造，仅由 <see cref="TryAcquire"/> 创建。</summary>
    /// <param name="mutex">已创建的互斥体，可为 null</param>
    private SingleInstance(Mutex? mutex) => _mutex = mutex;

    /// <summary>
    /// 尝试成为唯一实例：成功返回实例对象；
    /// 已有实例在运行时向对方广播通知并返回 null（调用方应立即退出）。
    /// </summary>
    /// <returns>成功返回实例对象；已有实例在运行返回 null</returns>
    public static SingleInstance? TryAcquire()
    {
        try
        {
            // initiallyOwned: false —— 只用 createdNew 判定唯一性，不需要线程所有权
            var mutex = new Mutex(false, MutexName, out var createdNew);
            if (createdNew) return new SingleInstance(mutex);
            mutex.Dispose();
        }
        catch (Exception ex)
        {
            // 互斥体创建失败（极少见）时放行：宁可偶发双实例，也不要拒绝启动
            Log.Warn($"单实例互斥体创建失败，本次放行启动：{ex.Message}");
            return new SingleInstance(null);
        }

        // 已有实例在运行：广播通知它弹气泡，然后由调用方退出
        Log.Info("检测到已有实例在运行，本次启动退出");
        NotifyExistingInstance();
        return null;
    }

    /// <summary>向已有实例广播“已在运行”消息（失败静默忽略，不影响退出）。</summary>
    private static void NotifyExistingInstance()
    {
        try
        {
            // HWND_BROADCAST = 0xFFFF：发送给系统中所有顶层窗口（含隐藏窗口）
            SendNotifyMessage(new IntPtr(0xFFFF), (uint)AlreadyRunningMessage, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // 通知失败不影响退出逻辑
        }
    }

    /// <summary>释放互斥体（程序退出时调用）。</summary>
    public void Dispose() => _mutex?.Dispose();

    /// <summary>向指定窗口广播消息（P/Invoke）。</summary>
    /// <param name="hWnd">目标窗口句柄，0xFFFF 表示广播</param>
    /// <param name="message">消息号</param>
    /// <param name="wParam">消息参数</param>
    /// <param name="lParam">消息参数</param>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SendNotifyMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    /// <summary>注册一个 Windows 广播消息（P/Invoke）。</summary>
    /// <param name="message">消息名称</param>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string message);
}