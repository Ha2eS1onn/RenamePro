using RenamePro.Tray;

namespace RenamePro;

/// <summary>
/// 程序入口：以托盘形式常驻后台，不显示主窗口、不出现控制台。
/// </summary>
static class Program
{
    /// <summary>
    /// 应用程序主入口点。
    /// </summary>
    [STAThread]
    static void Main()
    {
        // WinForms 全局初始化（高 DPI 等，见项目属性 ApplicationHighDpiMode）
        ApplicationConfiguration.Initialize();
        // 托盘上下文驱动消息循环（无主窗口）
        Application.Run(new TrayAppContext());
    }    
}