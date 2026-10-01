using System.Text;
using RenamePro.Conversion;
using RenamePro.Core;

namespace RenamePro;

/// <summary>
/// 文档转换排障入口（<c>RenamePro.exe --docdiagnose &lt;文件&gt; [目标扩展名]</c>）。
///
/// 为什么单独做这个入口：文档转换依赖外部引擎，出问题时"到底是引擎没找到、profile 没初始化、
/// 还是参数/权限问题"从托盘日志里不容易一眼判断。这里把同样的流程跑一遍并按步骤打印，
/// 用户把输出贴出来就能定位。
/// </summary>
internal static class DocDiagnose
{
    /// <summary>运行诊断。</summary>
    /// <param name="args">第一个元素是源文件路径，第二个（可选）是目标扩展名，默认 .pdf</param>
    /// <returns>进程退出码：0 = 转换成功</returns>
    public static int Run(string[] args)
    {
        var output = new StringBuilder();
        void Line(string text)
        {
            output.AppendLine(text);
            try { Console.WriteLine(text); } catch { /* 没有控制台时静默 */ }
        }

        if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
        {
            Line("用法：RenamePro.exe --docdiagnose <源文件> [目标扩展名，如 .pdf]");
            return 2;
        }

        var sourcePath = Path.GetFullPath(args[0]);
        var targetExt = args.Length > 1 && !string.IsNullOrWhiteSpace(args[1])
            ? PathRules.NormalizeExt(args[1].StartsWith('.') ? args[1] : "." + args[1])
            : ".pdf";

        AppConfig.EnsureLoaded();

        Line("=== RenamePro 文档转换诊断 ===");
        Line($"源文件  ：{sourcePath}");
        Line($"目标扩展：{targetExt}");
        Line($"配置文件：{AppConfig.FilePath}");
        Line("");
        Line(DocConverter.DescribeEnvironment());
        Line("");

        if (!File.Exists(sourcePath))
        {
            Line("[FAIL] 源文件不存在");
            return 1;
        }

        var sniffed = FormatSniffer.Sniff(sourcePath);
        Line($"[1/3] 文件头嗅探：{sniffed ?? "无法识别（纯文本）"}");
        var strict = !PathRules.IsTextLikeExtension(PathRules.NormalizeExt(Path.GetExtension(sourcePath)));
        var matches = FormatSniffer.MatchesExtension(sniffed, PathRules.NormalizeExt(Path.GetExtension(sourcePath)), strict);
        Line($"      与扩展名一致：{matches}");
        if (!matches)
        {
            Line("[FAIL] 内容与扩展名不符，正式流程会在这一步跳过（这是保护行为，不是缺陷）");
            return 1;
        }

        var plan = DocumentMatrix.Plan(PathRules.NormalizeExt(Path.GetExtension(sourcePath)), targetExt,
            AppConfig.Current.AllowPdfSource, out var reason);
        Line($"[2/3] 转换矩阵：{(plan == null ? "不支持 → " + reason : plan.Description)}");
        if (plan == null) return 1;

        Line($"[3/3] 真实转换中…");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var (outputPath, error) = DocConverter.DiagnoseAsync(sourcePath, targetExt).GetAwaiter().GetResult();
        watch.Stop();

        if (error != null)
        {
            Line($"[FAIL] 转换失败（{watch.Elapsed.TotalSeconds:0.0}s）：{error}");
            Line("      详细过程见同目录 log.txt 里的 [文档] 行");
            return 1;
        }

        var info = new FileInfo(outputPath!);
        Line($"[OK] 转换成功：{info.Length} 字节，耗时 {watch.Elapsed.TotalSeconds:0.0}s");
        Line($"     产物：{info.FullName}");
        return 0;
    }
}
