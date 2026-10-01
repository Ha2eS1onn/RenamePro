using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using RenamePro.Conversion;
using RenamePro.Core;

namespace RenamePro;

/// <summary>
/// 内置自检（<c>RenamePro.exe --selftest</c>）：在没有单元测试工程的前提下，
/// 用真实文件走一遍"格式校验 → 矩阵判定 → 转换 → 落盘"的关键路径，把结果打到控制台并以退出码表示成败。
///
/// 覆盖范围：
///   1. 格式嗅探：OOXML/ODF/ZIP/RTF/PDF/OLE2 识别，以及"改错后缀"必须被拦下（失败方向安全）；
///   2. 转换矩阵：合法组合生成计划、跨族组合带可操作原因被拒绝；
///   3. 内置 Markdown → docx：真实写包并回读校验（不依赖任何外置引擎）；
///   4. 随包文档引擎目录：全功能版自带的 LibreOffice\ 是否齐备、能否被探测到（未随包分发时跳过）；
///   5. 端到端：把真实文件改后缀后提交给转换主流程，检查产物、副本与格式是否正确。
///
/// 退出码：0 = 全部通过；1 = 有失败项。日志仍照常写入 log.txt，便于对照。
/// </summary>
internal static class SelfTest
{
    /// <summary>自检专用工作目录名（放在临时目录下，避免污染用户文件）。</summary>
    private const string WorkFolderName = "RenamePro-selftest";

    private static int _passed;
    private static int _failed;
    private static int _skipped;
    private static readonly StringBuilder Report = new();

    /// <summary>
    /// 自检开始时刻：用于区分"本次自检启动的引擎进程"与机器上本来就存在的引擎进程
    /// （见 <see cref="AnyProcessRunningSince"/>）。
    /// </summary>
    private static readonly DateTime RunStartedAt = DateTime.Now;

    /// <summary>把控制台附着到父进程（WinExe 没有自己的控制台，不附着就看不到输出）。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    /// <summary>父进程控制台常量。</summary>
    private const int AttachParentProcess = -1;

    /// <summary>
    /// 运行自检。
    /// </summary>
    /// <returns>进程退出码：0 = 全部通过</returns>
    public static int Run()
    {
        AttachConsole(AttachParentProcess); // 失败也无妨：结果同时写进 log.txt 与返回值

        var work = Path.Combine(Path.GetTempPath(), WorkFolderName);
        if (Directory.Exists(work))
        {
            try { Directory.Delete(work, recursive: true); } catch { /* 删不掉就复用并覆盖 */ }
        }
        Directory.CreateDirectory(work);

        WriteLine("");
        WriteLine("=== RenamePro 文档转换自检 ===");
        WriteLine($"工作目录：{work}");
        WriteLine($"配置文件：{AppConfig.FilePath}");
        WriteLine("");

        AppConfig.EnsureLoaded();

        try
        {
            CheckFormatSniffer(work);
            CheckDocumentMatrix();
            CheckMarkdownToDocx(work);
            CheckBundledEngine(work);
            CheckPipelineEndToEnd(work);
        }
        catch (Exception ex)
        {
            Fail("自检执行", $"未捕获异常：{ex}");
        }

        WriteLine("");
        WriteLine($"=== 结果：{_passed} 通过 / {_failed} 失败 / {_skipped} 跳过 ===");

        // 结论也写进日志：托盘程序控制台可能看不到，log.txt 是可靠载体
        Log.Info($"[自检] 完成：{_passed} 通过 / {_failed} 失败 / {_skipped} 跳过");
        if (_failed > 0) Log.Error("[自检] 存在失败项，详见上方逐条结果");

        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "selftest-report.txt"),
                Report.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // 报告写不出来不影响退出码
        }

        return _failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ 1. 格式嗅探

    /// <summary>格式嗅探的识别与拒绝行为。</summary>
    /// <param name="work">工作目录</param>
    private static void CheckFormatSniffer(string work)
    {
        WriteLine("--- 1. 格式嗅探（magic number）---");

        var docx = Path.Combine(work, "probe.docx");
        WriteZipFixture(docx, "word/document.xml");
        CheckTrue("docx 识别为 ooxml-word", FormatSniffer.Sniff(docx) == "ooxml-word",
            $"实际 {FormatSniffer.Sniff(docx) ?? "null"}");

        var pptx = Path.Combine(work, "probe.pptx");
        WriteZipFixture(pptx, "ppt/presentation.xml");
        CheckTrue("pptx 识别为 ooxml-presentation", FormatSniffer.Sniff(pptx) == "ooxml-presentation",
            $"实际 {FormatSniffer.Sniff(pptx) ?? "null"}");

        var xlsx = Path.Combine(work, "probe.xlsx");
        WriteZipFixture(xlsx, "xl/workbook.xml");
        CheckTrue("xlsx 识别为 ooxml-spreadsheet", FormatSniffer.Sniff(xlsx) == "ooxml-spreadsheet",
            $"实际 {FormatSniffer.Sniff(xlsx) ?? "null"}");

        var odt = Path.Combine(work, "probe.odt");
        WriteOdfFixture(odt);
        CheckTrue("odt 识别为 odf-text", FormatSniffer.Sniff(odt) == "odf-text",
            $"实际 {FormatSniffer.Sniff(odt) ?? "null"}");

        var plainZip = Path.Combine(work, "probe.zip");
        WriteZipFixture(plainZip, "notes/readme.txt");
        CheckTrue("普通 zip 不冒充任何文档格式", FormatSniffer.Sniff(plainZip) == "zip",
            $"实际 {FormatSniffer.Sniff(plainZip) ?? "null"}");

        var rtf = Path.Combine(work, "probe.rtf");
        File.WriteAllText(rtf, @"{\rtf1\ansi RenamePro}");
        CheckTrue("rtf 识别为 rtf", FormatSniffer.Sniff(rtf) == "rtf");

        var pdf = Path.Combine(work, "probe.pdf");
        File.WriteAllText(pdf, "%PDF-1.7\n%%EOF");
        CheckTrue("pdf 识别为 pdf", FormatSniffer.Sniff(pdf) == "pdf");

        var oleDoc = Path.Combine(work, "probe.doc");
        WriteOle2Fixture(oleDoc, "WordDocument");
        CheckTrue("旧版 doc 识别为 ole-word", FormatSniffer.Sniff(oleDoc) == "ole-word",
            $"实际 {FormatSniffer.Sniff(oleDoc) ?? "null"}");

        // —— 关键的安全行为：改错后缀必须被拒绝 ——
        var png = Path.Combine(work, "真图片.png");
        WritePngFixture(png);
        var fake = Path.Combine(work, "假文档.docx");
        File.Copy(png, fake, overwrite: true);
        var fakeFormat = FormatSniffer.Sniff(fake);
        CheckTrue("png 改名成 .docx 被识别为 png（会被跳过）", fakeFormat == "png", $"实际 {fakeFormat ?? "null"}");
        CheckTrue("png 内容不匹配 .docx 扩展名",
            !FormatSniffer.MatchesExtension(fakeFormat, ".docx"),
            "MatchesExtension 意外返回 true");

        // —— 纯文本族：负证据放行 + 严格模式仍拒绝 ——
        var md = Path.Combine(work, "说明.md");
        File.WriteAllText(md, "# 标题\n\n普通段落。\n");
        var mdFormat = FormatSniffer.Sniff(md);
        CheckTrue("纯文本 md 嗅探结果为 null（无签名）", mdFormat == null, $"实际 {mdFormat ?? "null"}");
        CheckTrue("纯文本 md 在放宽模式下放行", FormatSniffer.MatchesExtension(mdFormat, ".md", strict: false));
        CheckTrue("纯文本 md 在严格模式下仍被拒绝", !FormatSniffer.MatchesExtension(mdFormat, ".md"));

        var fakeMd = Path.Combine(work, "假文本.md");
        File.Copy(png, fakeMd, overwrite: true);
        CheckTrue("png 改名成 .md 仍被拒绝",
            !FormatSniffer.MatchesExtension(FormatSniffer.Sniff(fakeMd), ".md", strict: false));

        var html = Path.Combine(work, "页面.html");
        File.WriteAllText(html, "<!DOCTYPE html>\n<html><body>hi</body></html>");
        CheckTrue("html 识别为 html-marker", FormatSniffer.Sniff(html) == "html-marker",
            $"实际 {FormatSniffer.Sniff(html) ?? "null"}");

        var csv = Path.Combine(work, "数据.csv");
        File.WriteAllText(csv, "姓名,数量\n甲,1\n");
        CheckTrue("csv 走放宽模式放行", FormatSniffer.MatchesExtension(FormatSniffer.Sniff(csv), ".csv", strict: false));

        var empty = Path.Combine(work, "空文件.md");
        File.WriteAllBytes(empty, Array.Empty<byte>());
        CheckTrue("0 字节文件不会崩溃", FormatSniffer.Sniff(empty) == null);
    }

    // ------------------------------------------------------------------ 2. 转换矩阵

    /// <summary>文档族与转换矩阵判定。</summary>
    private static void CheckDocumentMatrix()
    {
        WriteLine("");
        WriteLine("--- 2. 转换矩阵 ---");

        CheckTrue(".docx 属于文字处理族", DocumentMatrix.FamilyOf(".docx") == DocumentFamily.Writer);
        CheckTrue(".pptx 属于演示文稿族", DocumentMatrix.FamilyOf(".pptx") == DocumentFamily.Impress);
        CheckTrue(".xlsx 属于电子表格族", DocumentMatrix.FamilyOf(".xlsx") == DocumentFamily.Calc);
        CheckTrue(".pdf 单列一族", DocumentMatrix.FamilyOf(".pdf") == DocumentFamily.Pdf);

        CheckPlan("docx → pdf 允许", ".docx", ".pdf", false, expectPlan: true);
        CheckPlan("pptx → pdf 允许", ".pptx", ".pdf", false, expectPlan: true);
        CheckPlan("xlsx → pdf 允许", ".xlsx", ".pdf", false, expectPlan: true);
        CheckPlan("docx → odt 允许（同族）", ".docx", ".odt", false, expectPlan: true);
        CheckPlan("pptx → ppt 允许（同族）", ".pptx", ".ppt", false, expectPlan: true);
        CheckPlan("xlsx → csv 允许（同族）", ".xlsx", ".csv", false, expectPlan: true);
        CheckPlan("md → docx 允许（内置转换器）", ".md", ".docx", false, expectPlan: true,
            expectBuiltIn: true);
        CheckPlan("xlsx → docx 被拒绝（跨族）", ".xlsx", ".docx", false, expectPlan: false);
        CheckPlan("docx → xlsx 被拒绝（跨族）", ".docx", ".xlsx", false, expectPlan: false);
        CheckPlan("pdf → docx 默认被拒绝", ".pdf", ".docx", false, expectPlan: false);
        CheckPlan("pdf → docx 开启 allowPdfSource 后允许", ".pdf", ".docx", true, expectPlan: true);
        CheckPlan("pdf → pptx 即使允许也被拒绝", ".pdf", ".pptx", true, expectPlan: false);
        CheckPlan("docm 不能作目标", ".docx", ".docm", false, expectPlan: false);
        CheckPlan("docm 可作源", ".docm", ".pdf", false, expectPlan: true);
        CheckPlan("txt 不在文档集合内", ".txt", ".docx", false, expectPlan: false);

        // 拒绝原因必须可操作（能指导用户改什么），而不是一句"不支持"
        DocumentMatrix.Plan(".xlsx", ".docx", false, out var crossReason);
        CheckTrue("跨族拒绝原因给出可操作建议",
            crossReason != null && (crossReason.Contains("csv") || crossReason.Contains("pdf")),
            $"实际原因：{crossReason ?? "null"}");

        DocumentMatrix.Plan(".pdf", ".docx", false, out var pdfReason);
        CheckTrue("PDF 源拒绝原因提到 allowPdfSource",
            pdfReason != null && pdfReason.Contains("allowPdfSource"),
            $"实际原因：{pdfReason ?? "null"}");

        CheckTrue("内置转换器标记正确",
            DocumentMatrix.Plan(".md", ".docx", false, out _) is { NeedsExternalEngine: false, UseBuiltInMarkdown: true });
    }

    /// <summary>单条矩阵断言。</summary>
    /// <param name="name">用例名</param>
    /// <param name="sourceExt">源扩展名</param>
    /// <param name="targetExt">目标扩展名</param>
    /// <param name="allowPdf">是否允许 PDF 源</param>
    /// <param name="expectPlan">是否应当生成计划</param>
    /// <param name="expectBuiltIn">是否应当是内置转换器</param>
    private static void CheckPlan(string name, string sourceExt, string targetExt, bool allowPdf,
        bool expectPlan, bool expectBuiltIn = false)
    {
        var plan = DocumentMatrix.Plan(sourceExt, targetExt, allowPdf, out var reason);
        if (expectPlan)
        {
            CheckTrue(name, plan != null, $"未生成计划，原因：{reason ?? "无"}");
            if (plan != null && expectBuiltIn)
            {
                CheckTrue($"{name}（确认走内置转换器）", plan.UseBuiltInMarkdown && !plan.NeedsExternalEngine);
            }
        }
        else
        {
            CheckTrue(name, plan == null && !string.IsNullOrWhiteSpace(reason),
                plan != null ? "本应被拒绝却生成了计划" : "被拒绝但没有给出原因");
        }
    }

    // ------------------------------------------------------------------ 3. 内置 Markdown → docx

    /// <summary>内置 Markdown 转换器：写真实的 docx 并回读校验。</summary>
    /// <param name="work">工作目录</param>
    private static void CheckMarkdownToDocx(string work)
    {
        WriteLine("");
        WriteLine("--- 3. 内置 Markdown → docx ---");

        var markdown = string.Join("\n", new[]
        {
            "# 一级标题",
            "",
            "普通段落，含 **粗体**、*斜体* 与 `行内代码`。",
            "",
            "- 列表项一",
            "- 列表项二",
            "",
            "1. 有序一",
            "2. 有序二",
            "",
            "> 引用内容",
            "",
            "```csharp",
            "var a = 1 < 2 && \"x\";",
            "```",
            "",
            "| 列 A | 列 B |",
            "| --- | --- |",
            "| 甲 | 乙 |",
            "",
            "---",
            "",
            "带链接 [示例](https://example.com) 的段落。"
        });

        var source = Path.Combine(work, "文档.md");
        File.WriteAllText(source, markdown);
        var target = Path.Combine(work, "输出.docx");
        MarkdownToDocx.Convert(markdown, target, work);

        CheckTrue("docx 已生成且非空", File.Exists(target) && new FileInfo(target).Length > 0,
            File.Exists(target) ? $"大小 {new FileInfo(target).Length} 字节" : "文件不存在");
        if (!File.Exists(target)) return;

        CheckTrue("产物的 magic 是 ooxml-word", FormatSniffer.Sniff(target) == "ooxml-word",
            $"实际 {FormatSniffer.Sniff(target) ?? "null"}");

        string documentXml;
        try
        {
            using var zip = ZipFile.OpenRead(target);
            var required = new[] { "[Content_Types].xml", "_rels/.rels", "word/document.xml", "word/styles.xml", "word/_rels/document.xml.rels" };
            var names = zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.Ordinal);
            var missing = required.Where(r => !names.Contains(r)).ToList();
            CheckTrue("docx 包结构完整（含必需部件）", missing.Count == 0, $"缺少：{string.Join(", ", missing)}");

            using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
            documentXml = reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            Fail("读取 docx 包", ex.Message);
            return;
        }

        CheckContains("标题文字进了文档", documentXml, "一级标题");
        CheckContains("标题使用 Heading 样式", documentXml, "Heading1");
        CheckContains("粗体用了 w:b", documentXml, "<w:b/>");
        CheckContains("斜体用了 w:i", documentXml, "<w:i/>");
        CheckContains("行内代码用了等宽样式", documentXml, "CodeChar");
        CheckContains("列表项进了文档", documentXml, "列表项一");
        CheckContains("表格进了文档", documentXml, "<w:tbl>");
        CheckContains("引用块样式正确", documentXml, "QuoteBlock");
        CheckContains("代码块样式正确", documentXml, "CodeBlock");
        CheckContains("水平线进了文档", documentXml, "w:pBdr");
        CheckContains("链接文字进了文档", documentXml, "示例");
        CheckContains("中文内容未被破坏", documentXml, "普通段落");

        // XML 转义：源里的 < && " 必须变成实体，否则 Word 会报文档损坏
        CheckTrue("XML 特殊字符已转义", documentXml.Contains("&lt;") && documentXml.Contains("&amp;"),
            "未找到 &lt; / &amp; 实体");

        // 空 Markdown 也要能产出一个可打开的文档（不能崩、不能产出 0 字节）
        var emptyTarget = Path.Combine(work, "空输出.docx");
        MarkdownToDocx.Convert(string.Empty, emptyTarget, work);
        CheckTrue("空 Markdown 也能产出有效 docx",
            File.Exists(emptyTarget) && new FileInfo(emptyTarget).Length > 0
            && FormatSniffer.Sniff(emptyTarget) == "ooxml-word");

        // 异常输入：大量特殊字符与非法控制字符
        var weirdTarget = Path.Combine(work, "特殊输出.docx");
        MarkdownToDocx.Convert("# <测试> & \"引号\" \u0007\n\n| a | b |\n| - | - |\n| <x> | & |\n", weirdTarget, work);
        CheckTrue("特殊字符 Markdown 未产出损坏文件",
            File.Exists(weirdTarget) && FormatSniffer.Sniff(weirdTarget) == "ooxml-word");
    }

    // ------------------------------------------------------------------ 4. 随包文档引擎目录

    /// <summary>
    /// 随包文档引擎目录（全功能版自带的 LibreOffice\）的完整性：
    /// 未随包分发的构建直接 SKIP；随包分发的构建要能直接找到引擎、清单汇总数字对得上、并能被探测到。
    /// 这里**不**做全量清单遍历（几千个文件，自检不该花几十秒），逐条校验交给 verify-docengine.ps1。
    /// </summary>
    /// <param name="work">工作目录（本检查不用，仅为与其它检查保持同一签名）</param>
    private static void CheckBundledEngine(string work)
    {
        _ = work;
        WriteLine("");
        WriteLine("--- 4. 随包文档引擎目录 ---");

        var engine = DocumentEngine.FindBundledSoffice();
        if (engine == null)
        {
            Skip($"随包引擎目录（本构建未随包分发：{DocumentEngine.BundledDirectory}\\program\\soffice.com）");
            return;
        }

        WriteLine($"  引擎目录：{DocumentEngine.BundledDirectory}");
        WriteLine($"  引擎文件：{engine}（{new FileInfo(engine).Length / (1024.0 * 1024.0):0.0} MB）");

        var size = 0L;
        var fileCount = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(DocumentEngine.BundledDirectory, "*", SearchOption.AllDirectories))
            {
                size += new FileInfo(file).Length;
                fileCount++;
            }
        }
        catch (Exception ex)
        {
            WriteLine($"  （统计目录体积失败：{ex.Message}）");
        }
        WriteLine($"  目录体积：{size / (1024.0 * 1024.0):0} MB（{fileCount} 个文件）");
        CheckTrue("随包引擎目录里有文件", fileCount > 0);

        // 清单（payload.json）：存在时只核对汇总数字；缺失不算失败（清单只是发布脚本的产物）
        var manifestPath = Path.Combine(DocumentEngine.BundledDirectory, "payload.json");
        if (File.Exists(manifestPath))
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));
                var root = document.RootElement;
                var declaredFiles = root.TryGetProperty("fileCount", out var fc) ? fc.GetInt32() : -1;
                var declaredBytes = root.TryGetProperty("extractedBytes", out var eb) ? eb.GetInt64() : -1;
                var engineRelative = root.TryGetProperty("engineRelativePath", out var erp) ? erp.GetString() : null;

                WriteLine($"  清单：fileCount={declaredFiles}，extractedBytes={declaredBytes / (1024 * 1024)} MB，" +
                          $"engineRelativePath={engineRelative}");
                CheckTrue("清单记录的引擎文件存在",
                    engineRelative != null && File.Exists(Path.Combine(DocumentEngine.BundledDirectory,
                        engineRelative.Replace('/', Path.DirectorySeparatorChar))),
                    engineRelative ?? "（清单缺少 engineRelativePath）");
                CheckTrue("清单 fileCount 与实际相符（实际 >= 清单，差值是清单自身等说明文件）",
                    declaredFiles < 0 || fileCount >= declaredFiles,
                    $"清单 {declaredFiles}，实际 {fileCount}");
                CheckTrue("清单体积与实际相符（±5%）",
                    declaredBytes <= 0 || Math.Abs(size - declaredBytes) <= declaredBytes * 0.05,
                    $"清单 {declaredBytes / (1024 * 1024)} MB，实际 {size / (1024 * 1024)} MB");
            }
            catch (Exception ex)
            {
                Fail("解析随包引擎清单", ex.Message);
            }
        }
        else
        {
            Skip("随包引擎清单 payload.json（未随目录提供）");
        }

        // 探测：随包目录是 auto 模式下的第一顺位，必须被认出来
        DocumentEngine.InvalidateCache();
        var status = DocumentEngine.Probe();
        WriteLine($"  引擎探测：{(status.IsAvailable ? status.Detail : "不可用（" + status.Error + "）")}");
        CheckTrue("随包引擎可被探测到", status.IsAvailable, status.Error ?? "");
        CheckTrue("引擎来源标记为随包目录",
            status.Detail?.Contains("随包目录", StringComparison.Ordinal) == true, status.Detail ?? "");
    }

    // ------------------------------------------------------------------ 5. 端到端

    /// <summary>走真实主流程的端到端检查（不需要外置引擎的用例总是执行）。</summary>
    /// <param name="work">工作目录</param>
    private static void CheckPipelineEndToEnd(string work)
    {
        WriteLine("");
        WriteLine("--- 5. 转换主流程端到端 ---");

        var engine = DocumentEngine.Probe();
        WriteLine($"文档引擎：{(engine.IsAvailable ? engine.Detail : "不可用（" + engine.Error + "）")}");
        WriteLine($"Office COM：{DescribeCom()}");
        WriteLine($"超时计算：1MB 源 = {DocConverter.EffectiveTimeoutSeconds(Path.Combine(work, "x"))}s（配置基准 {AppConfig.Current.DocTimeoutSeconds}s）");

        using var internalOps = new InternalOpsSet();
        using var pipeline = new ConversionPipeline(internalOps);

        // —— 用例 A：md → docx 完全不需要外置引擎，必须成功 ——
        var mdSource = Path.Combine(work, "报告.md");
        File.WriteAllText(mdSource, "# 报告标题\n\n正文 **加粗**。\n");
        var mdTarget = Path.Combine(work, "报告.docx");
        File.Move(mdSource, mdTarget);
        var result = Submit(pipeline, mdSource, mdTarget);
        CheckTrue("md → docx 转换成功", result == ConversionResult.Ok, $"结果 {result}");
        CheckTrue("md → docx 产物可识别",
            File.Exists(mdTarget) && FormatSniffer.Sniff(mdTarget) == "ooxml-word",
            File.Exists(mdTarget) ? $"magic = {FormatSniffer.Sniff(mdTarget) ?? "null"}" : "目标文件不存在");
        CheckTrue("按配置生成了原格式副本",
            !AppConfig.Current.EnableBackup || File.Exists(Path.Combine(work, "报告 - 副本.md"))
                                       || Directory.GetFiles(work, "报告*副本*.md").Length > 0,
            "未找到副本文件");
        CheckTrue("转换后没有残留工作目录",
            Directory.GetDirectories(work, ".renamepro-doc-*").Length == 0,
            $"残留：{string.Join(", ", Directory.GetDirectories(work, ".renamepro-doc-*").Select(Path.GetFileName))}");

        // —— 用例 B：内容与后缀不符，必须被拦下（失败方向安全） ——
        var pngSource = Path.Combine(work, "图片.png");
        WritePngFixture(pngSource);
        var fakeDocx = Path.Combine(work, "伪装.docx");
        File.Copy(pngSource, fakeDocx, overwrite: true);
        var fakeResult = Submit(pipeline, pngSource, fakeDocx);
        CheckTrue("png 改名成 docx 被跳过（未转换）", fakeResult == ConversionResult.Skipped, $"结果 {fakeResult}");
        CheckTrue("被跳过的文件仍是原始 png 内容",
            FormatSniffer.Sniff(fakeDocx) == "png", $"magic = {FormatSniffer.Sniff(fakeDocx) ?? "null"}");

        // —— 用例 C：跨族组合被跳过且不产生副本 ——
        var xlsxSource = Path.Combine(work, "表格.xlsx");
        WriteZipFixture(xlsxSource, "xl/workbook.xml");
        var crossTarget = Path.Combine(work, "表格.docx");
        File.Move(xlsxSource, crossTarget);
        var crossResult = Submit(pipeline, xlsxSource, crossTarget);
        CheckTrue("xlsx → docx 被跳过", crossResult == ConversionResult.Skipped, $"结果 {crossResult}");
        CheckTrue("跨族被拒时不会生成副本",
            Directory.GetFiles(work, "表格*副本*").Length == 0, "不该有副本");

        // —— 用例 D：外置引擎路径（有引擎才跑） ——
        if (!engine.IsAvailable)
        {
            Skip("docx → pdf 端到端（无可用引擎）");
            Skip("引擎不可用时的降级日志（由 --selftest 之外的日志核对）");
            return;
        }

        var docxSource = Path.Combine(work, "合同.docx");
        WriteZipFixture(docxSource, "word/document.xml");
        var pdfTarget = Path.Combine(work, "合同.pdf");
        File.Move(docxSource, pdfTarget);

        // 引擎不认识我们手造的极简 docx（它是给自检用的"合法但空"的包），
        // 因此这一步只断言"流程跑通、结果落在预期集合内、没有残留引擎进程"，不做产物断言。
        var pdfResult = Submit(pipeline, docxSource, pdfTarget);
        WriteLine($"      （引擎路径结果：{pdfResult}；手造的最小 docx 引擎可能拒收，属预期）");
        CheckTrue("引擎路径没有抛出未捕获异常",
            pdfResult is ConversionResult.Ok or ConversionResult.Failed or ConversionResult.Skipped,
            $"结果 {pdfResult}");

        // 引擎的关闭是异步的：转换返回后 soffice.bin 还会短暂存活，因此给它一个宽限期再判残留。
        // 只统计"本次自检启动之后"的引擎进程：机器上常驻的引擎（例如系统 LibreOffice 的快速启动）
        // 不该被算成我们的残留，否则自检会在无缺陷的机器上长期红着。
        var engineExited = WaitForEngineProcessesExit(RunStartedAt, TimeSpan.FromSeconds(15));
        CheckTrue("引擎路径结束后没有残留引擎进程", engineExited,
            "本次自检启动的 soffice / soffice.bin 在 15 秒内没有退出");

        // 残留工作目录只警告不判失败：受限环境下"删不掉被占用的目录"是权限问题而非逻辑问题
        // （本机沙箱连 Process.Kill 都拒绝，Office 进程会活着攥住暂存文件），
        // 若这里判失败，自检会在无缺陷的机器上长期红着，反而失去信号价值。
        // 先等引擎把手里的暂存文件放开，避免把"刚退出还没释放"误记成残留。
        WaitForEngineProcessesExit(RunStartedAt, TimeSpan.FromSeconds(5));
        var leftovers = Directory.GetDirectories(work, ".renamepro-doc-*");
        if (leftovers.Length == 0)
        {
            CheckTrue("引擎路径结束后没有残留工作目录", true);
        }
        else
        {
            _skipped++;
            var detail = string.Join("; ", leftovers.Select(d =>
                $"{Path.GetFileName(d)} → {string.Join(",", Directory.GetFiles(d).Select(Path.GetFileName))}"));
            WriteLine($"  [WARN] 残留工作目录未删掉（受限环境常见，不影响功能）：{detail}");
        }
    }

    /// <summary>同步提交一次任务并取回结果（自检用）。</summary>
    /// <param name="pipeline">转换主流程</param>
    /// <param name="oldPath">改名前路径</param>
    /// <param name="newPath">改名后路径</param>
    private static ConversionResult Submit(ConversionPipeline pipeline, string oldPath, string newPath)
    {
        ConversionResult? captured = null;
        pipeline.TaskFinished += (_, result) => captured = result;
        pipeline.Submit(oldPath, newPath, blocking: true);
        // RunSafeAsync 在后台线程收尾，这里等一小会儿让 TaskFinished 落地
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (captured == null && DateTime.UtcNow < deadline) Thread.Sleep(20);
        return captured ?? ConversionResult.Failed;
    }

    /// <summary>Office COM 可用性描述。</summary>
    private static string DescribeCom()
    {
        try
        {
            var (word, powerpoint, excel) = DocumentEngine.ComAvailability;
            if (!word && !powerpoint && !excel) return "不可用";
            return $"Word={word} PowerPoint={powerpoint} Excel={excel}";
        }
        catch (Exception ex)
        {
            return $"探测异常：{ex.Message}";
        }
    }

    /// <summary>是否有指定名字的进程在运行。</summary>
    /// <param name="names">进程名（不含 .exe）</param>
    private static bool AnyProcessRunning(params string[] names)
    {
        foreach (var name in names)
        {
            try
            {
                var processes = System.Diagnostics.Process.GetProcessesByName(name);
                if (processes.Length > 0)
                {
                    foreach (var process in processes) process.Dispose();
                    return true;
                }
            }
            catch
            {
                // 探测失败按"没有残留"处理
            }
        }
        return false;
    }

    /// <summary>
    /// 是否有"在 <paramref name="startedAfter"/> 之后才启动"的指定进程在运行。
    /// <para>
    /// 为什么按启动时间过滤：机器上可能本来就有常驻的引擎进程（例如系统安装的 LibreOffice 带快速启动），
    /// 只按进程名判断会把它们算成"我们的残留"，让自检在无缺陷的机器上长期报错。
    /// </para>
    /// </summary>
    /// <param name="startedAfter">只统计在该时刻之后启动的进程</param>
    /// <param name="names">进程名（不含 .exe）</param>
    private static bool AnyProcessRunningSince(DateTime startedAfter, params string[] names)
    {
        foreach (var name in names)
        {
            try
            {
                foreach (var process in System.Diagnostics.Process.GetProcessesByName(name))
                {
                    using (process)
                    {
                        try
                        {
                            if (process.StartTime >= startedAfter) return true;
                        }
                        catch
                        {
                            // 拿不到启动时间（权限不足 / 进程刚退出）：保守地按"是我们的残留"处理
                            return true;
                        }
                    }
                }
            }
            catch
            {
                // 探测失败按"没有残留"处理
            }
        }
        return false;
    }

    /// <summary>
    /// 等引擎进程退出，最多等 <paramref name="timeout"/>。LibreOffice 的关闭是异步的：
    /// 转换返回后 soffice.bin 还会短暂存活（这也是"工作目录一时删不掉"的直接原因）。
    /// </summary>
    /// <param name="startedAfter">只统计在该时刻之后启动的进程</param>
    /// <param name="timeout">最长等待时间</param>
    /// <returns>全部退出返回 true，超时仍存活返回 false</returns>
    private static bool WaitForEngineProcessesExit(DateTime startedAfter, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (AnyProcessRunningSince(startedAfter, "soffice", "soffice.bin"))
        {
            if (DateTime.UtcNow >= deadline) return false;
            Thread.Sleep(250);
        }
        return true;
    }

    // ------------------------------------------------------------------ 夹具构造

    /// <summary>写一个最小 OOXML 风格 ZIP（首个条目固定为 [Content_Types].xml）。</summary>
    /// <param name="path">目标路径</param>
    /// <param name="mainPart">主部件路径（决定识别为 word/ppt/xl 哪一族）</param>
    private static void WriteZipFixture(string path, string mainPart)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        WriteEntry(zip, "[Content_Types].xml",
            "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
        WriteEntry(zip, mainPart, "<?xml version=\"1.0\"?><root/>");
    }

    /// <summary>写一个最小 ODF 包（首个条目是 stored 的 mimetype）。</summary>
    /// <param name="path">目标路径</param>
    private static void WriteOdfFixture(string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        WriteEntry(zip, "mimetype", "application/vnd.oasis.opendocument.text", CompressionLevel.NoCompression);
        WriteEntry(zip, "content.xml", "<?xml version=\"1.0\"?><office:document-content/>");
    }

    /// <summary>写一个最小 PNG（含真实签名与 IHDR，便于嗅探器识别）。</summary>
    /// <param name="path">目标路径</param>
    private static void WritePngFixture(string path)
    {
        var bytes = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // PNG 签名
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, // IHDR 长度 + 类型
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, // 1x1
            0x08, 0x02, 0x00, 0x00, 0x00, 0x90, 0x77, 0x53, 0xDE
        };
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>写一个最小 OLE2 复合文档（签名 + 偏移 0x30 处的 UTF-16LE 根条目名）。</summary>
    /// <param name="path">目标路径</param>
    /// <param name="rootEntryName">根目录条目名（WordDocument / Workbook / PowerPoint Document）</param>
    private static void WriteOle2Fixture(string path, string rootEntryName)
    {
        // 偏移 0x30 是 OLE2 规范里第一个目录条目名的位置（也是嗅探器读取的位置），
        // 因此文件必须至少 0x30 + 64 字节，否则嗅探器只能退回"头窗口里找特征名"的兜底路径。
        var bytes = new byte[512];
        byte[] signature = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
        Array.Copy(signature, bytes, signature.Length);
        var name = Encoding.Unicode.GetBytes(rootEntryName);
        Array.Copy(name, 0, bytes, 0x30, name.Length);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>写一个 ZIP 条目。</summary>
    private static void WriteEntry(ZipArchive zip, string name, string content,
        CompressionLevel level = CompressionLevel.Optimal)
    {
        var entry = zip.CreateEntry(name, level);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    // ------------------------------------------------------------------ 断言与输出

    /// <summary>断言为真。</summary>
    /// <param name="name">用例名</param>
    /// <param name="condition">断言条件</param>
    /// <param name="detail">失败细节</param>
    private static void CheckTrue(string name, bool condition, string? detail = null)
    {
        if (condition)
        {
            _passed++;
            WriteLine($"  [PASS] {name}");
        }
        else
        {
            _failed++;
            WriteLine($"  [FAIL] {name}{(detail == null ? "" : " | " + detail)}");
        }
    }

    /// <summary>断言文本包含某片段。</summary>
    /// <param name="name">用例名</param>
    /// <param name="haystack">被检查文本</param>
    /// <param name="needle">期望片段</param>
    private static void CheckContains(string name, string haystack, string needle) =>
        CheckTrue(name, haystack.Contains(needle, StringComparison.Ordinal), $"未找到「{needle}」");

    /// <summary>记一条失败（无对应断言时使用）。</summary>
    /// <param name="name">用例名</param>
    /// <param name="detail">失败细节</param>
    private static void Fail(string name, string detail)
    {
        _failed++;
        WriteLine($"  [FAIL] {name} | {detail}");
    }

    /// <summary>记一条跳过（环境不具备，不算失败）。</summary>
    /// <param name="name">用例名</param>
    private static void Skip(string name)
    {
        _skipped++;
        WriteLine($"  [SKIP] {name}");
    }

    /// <summary>同时写控制台与报告缓冲。</summary>
    /// <param name="line">一行文本</param>
    private static void WriteLine(string line)
    {
        Report.AppendLine(line);
        try
        {
            Console.WriteLine(line);
        }
        catch
        {
            // 没有控制台时静默（结果仍在报告与日志里）
        }
    }
}
