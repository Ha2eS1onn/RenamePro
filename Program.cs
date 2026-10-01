using RenamePro.Core;
using RenamePro.Tray;

namespace RenamePro;

/// <summary>
/// 程序入口：以托盘形式常驻后台，不显示主窗口、不出现控制台。
/// 带 <c>--selftest</c> 参数时改为运行内置自检（见 <see cref="SelfTest"/>），跑完即退出。
/// </summary>
static class Program
{
    /// <summary>
    /// 应用程序主入口点。先做单实例检查：已有实例在运行时通知对方并退出，绝不产生第二个托盘图标。
    /// </summary>
    /// <param name="args">命令行参数；<c>--selftest</c> 表示运行内置自检而不是启动托盘</param>
    [STAThread]
    static void Main(string[] args)
    {
        // 单实例保护（互斥体持有到进程退出）
        using var singleInstance = SingleInstance.TryAcquire();
        if (singleInstance == null)
        {
            // 已有实例：自检要的是可观测输出，这里额外提示一句，避免"跑了却什么都没看到"
            if (IsSelfTest(args))
            {
                Console.Error.WriteLine("RenamePro 已在运行；自检需要独占运行，请先从托盘菜单退出后重试。");
            }
            return;
        }

        // 内置自检：不启动托盘、不建监听，走一遍格式校验与转换关键路径后以退出码表示成败
        if (IsSelfTest(args))
        {
            Environment.ExitCode = SelfTest.Run();
            return;
        }

        // 文档转换排障：对给定文件真实跑一次转换，打印引擎信息与逐步结果
        var diagnoseIndex = Array.FindIndex(args, a =>
            string.Equals(a, "--docdiagnose", StringComparison.OrdinalIgnoreCase));
        if (diagnoseIndex >= 0 && diagnoseIndex + 1 < args.Length)
        {
            Environment.ExitCode = DocDiagnose.Run(args[(diagnoseIndex + 1)..]);
            return;
        }

        // WinForms 全局初始化（高 DPI 等，见项目属性 ApplicationHighDpiMode）
        ApplicationConfiguration.Initialize();
        // 托盘上下文驱动消息循环（无主窗口）
        Application.Run(new TrayAppContext());
    }

    /// <summary>命令行里是否请求了自检。</summary>
    /// <param name="args">命令行参数</param>
    private static bool IsSelfTest(string[] args)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--selftest", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
