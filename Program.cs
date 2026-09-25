using RenamePro.Core;
using RenamePro.Tray;

namespace RenamePro;

/// <summary>
/// 程序入口：以托盘形式常驻后台，不显示主窗口、不出现控制台。
/// </summary>
static class Program
{
    /// <summary>
    /// 应用程序主入口点。先做单实例检查：已有实例在运行时通知对方并退出，绝不产生第二个托盘图标。
    /// </summary>
    [STAThread]
    static void Main()
    {
        // 单实例保护（互斥体持有到进程退出）
        using var singleInstance = SingleInstance.TryAcquire();
        if (singleInstance == null) return;

        // WinForms 全局初始化（高 DPI 等，见项目属性 ApplicationHighDpiMode）
        ApplicationConfiguration.Initialize();
        // 托盘上下文驱动消息循环（无主窗口）
        Application.Run(new TrayAppContext());
    }    
}