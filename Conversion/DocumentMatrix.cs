namespace RenamePro.Conversion;

/// <summary>文档族：决定"跨族转换"是否成立（PDF 视为无族，可作任意族的源/目标）。</summary>
public enum DocumentFamily
{
    /// <summary>未知/不支持（不在文档扩展名集合内）。</summary>
    None,

    /// <summary>文字处理：docx / doc / odt / rtf / md / html / csv(文本导出) 等。</summary>
    Writer,

    /// <summary>演示文稿：pptx / ppt / odp。</summary>
    Impress,

    /// <summary>电子表格：xlsx / xls / ods / csv / tsv。</summary>
    Calc,

    /// <summary>PDF：既不是源族的成员也不是目标族的成员，可跨族。</summary>
    Pdf
}

/// <summary>
/// 一次文档转换的判定结果（只依赖源/目标扩展名与配置，不触发任何引擎探测或 IO）。
/// </summary>
public sealed class DocumentPlan
{
    /// <summary>源所在文档族。</summary>
    public DocumentFamily SourceFamily { get; init; }

    /// <summary>目标所在文档族。</summary>
    public DocumentFamily TargetFamily { get; init; }

    /// <summary>目标扩展名（不带点，写进 --convert-to 的第一段）。</summary>
    public string TargetExtForFilter { get; init; } = string.Empty;

    /// <summary>LibreOffice 的过滤器名（如 writer_pdf_Export / "MS Word 2007 XML"）；空串表示交给引擎默认导出。</summary>
    public string FilterName { get; init; } = string.Empty;

    /// <summary>LibreOffice 过滤器选项（如 "UTF8"）；null 表示不带选项。</summary>
    public string? FilterOptions { get; init; }

    /// <summary>是否需要外置引擎（false = 由内置 Markdown 转换器完成，无需任何引擎）。</summary>
    public bool NeedsExternalEngine { get; init; } = true;

    /// <summary>是否使用内置 Markdown 转换器。</summary>
    public bool UseBuiltInMarkdown { get; init; }

    /// <summary>中文说明（写进日志与进度框）。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>需要提示的有损项（写日志，不阻断转换）。</summary>
    public string? LossNote { get; init; }
}

/// <summary>
/// 文档转换矩阵：把"源扩展名 → 目标扩展名"翻译成可执行的计划，或给出可操作的拒绝原因。
///
/// 规则（刻意保持简单、可解释）：
///   1. 同一文档族内互转：允许（Writer↔Writer、Impress↔Impress、Calc↔Calc）；
///   2. PDF 作目标：任意族都允许（PDF 是版式终点，不存在"错族"问题）；
///   3. PDF 作源：仅当 allowPdfSource=true，且只能转成 Writer 族或 PDF；
///   4. 其余跨族组合（docx→xlsx、xlsx→docx、pptx→docx 等）：拒绝，并给出可操作的替代建议。
///      理由：把工作簿变成"散文"文件在事后无法被发现，违背"内容真的是新格式"这个契约。
///   5. 宏格式（docm/xlsm/pptm）只作源，永不产出（不主动生成可执行宏的文档）。
///   6. markdown → docx 由内置转换器完成（不依赖任何外置引擎，因此始终可用）。
/// </summary>
public static class DocumentMatrix
{
    /// <summary>Writer 族扩展名（含 Markdown/HTML/CSV 这些由文字处理引擎导入导出的格式）。</summary>
    private static readonly HashSet<string> WriterExtensions = new(StringComparer.Ordinal)
    {
        ".docx", ".docm", ".dotx", ".doc", ".odt", ".ott", ".rtf", ".md", ".markdown", ".html", ".htm", ".xhtml"
    };

    /// <summary>Impress 族扩展名。</summary>
    private static readonly HashSet<string> ImpressExtensions = new(StringComparer.Ordinal)
    {
        ".pptx", ".pptm", ".potx", ".ppt", ".odp", ".otp"
    };

    /// <summary>Calc 族扩展名。</summary>
    private static readonly HashSet<string> CalcExtensions = new(StringComparer.Ordinal)
    {
        ".xlsx", ".xlsm", ".xltx", ".xls", ".ods", ".ots", ".csv", ".tsv"
    };

    /// <summary>宏格式：可作为源，永不作为目标。</summary>
    private static readonly HashSet<string> MacroExtensions = new(StringComparer.Ordinal)
    {
        ".docm", ".xlsm", ".pptm"
    };

    /// <summary>
    /// LibreOffice 过滤器名（目标扩展名 → 过滤器）。缺省表示使用引擎默认导出。
    /// 只收录真正可达的组合：目标扩展名所属文档族必须与源同族或是 pdf，
    /// 否则该条目永远不会被用到（例如 .csv 属于 Calc 族，Writer 源转 .csv 会被跨族规则拒掉）。
    /// </summary>
    private static readonly Dictionary<string, string> WriterFilters = new(StringComparer.Ordinal)
    {
        [".pdf"] = "writer_pdf_Export",
        [".docx"] = "MS Word 2007 XML",
        [".doc"] = "MS Word 97",
        [".odt"] = "writer8",
        [".ott"] = "writer8_template",
        [".rtf"] = "Rich Text Format",
        [".md"] = "Markdown",
        [".markdown"] = "Markdown",
        [".html"] = "HTML (StarWriter)",
        [".htm"] = "HTML (StarWriter)",
        [".xhtml"] = "XHTML Writer File"
    };

    /// <summary>Impress 族过滤器（目标扩展名 → 过滤器）。</summary>
    private static readonly Dictionary<string, string> ImpressFilters = new(StringComparer.Ordinal)
    {
        [".pdf"] = "impress_pdf_Export",
        [".pptx"] = "Impress MS PowerPoint 2007 XML",
        [".ppt"] = "MS PowerPoint 97",
        [".odp"] = "impress8",
        [".otp"] = "impress8_template"
    };

    /// <summary>Calc 族过滤器（目标扩展名 → 过滤器）。</summary>
    private static readonly Dictionary<string, string> CalcFilters = new(StringComparer.Ordinal)
    {
        [".pdf"] = "calc_pdf_Export",
        [".xlsx"] = "Calc MS Excel 2007 XML",
        [".xls"] = "MS Excel 97",
        [".ods"] = "calc8",
        [".ots"] = "calc8_template",
        [".csv"] = "Text - txt - csv (StarCalc)",
        [".tsv"] = "Text - txt - csv (StarCalc)"
    };

    /// <summary>
    /// PDF 作为源时的目标过滤器：LibreOffice 用内置的 PDF 导入（Poppler）建一个 Draw 文档，
    /// 再"另存为"文字处理格式，因此过滤器名与 Writer 族同名。
    /// </summary>
    private static readonly Dictionary<string, string> PdfSourceFilters = new(StringComparer.Ordinal)
    {
        [".docx"] = "MS Word 2007 XML",
        [".doc"] = "MS Word 97",
        [".odt"] = "writer8",
        [".rtf"] = "Rich Text Format",
        [".html"] = "HTML (StarWriter)",
        [".htm"] = "HTML (StarWriter)",
        [".md"] = "Markdown",
        [".markdown"] = "Markdown"
    };

    /// <summary>判断扩展名所属文档族。</summary>
    /// <param name="ext">归一化扩展名（小写带点）</param>
    public static DocumentFamily FamilyOf(string ext)
    {
        if (WriterExtensions.Contains(ext)) return DocumentFamily.Writer;
        if (ImpressExtensions.Contains(ext)) return DocumentFamily.Impress;
        if (CalcExtensions.Contains(ext)) return DocumentFamily.Calc;
        if (ext == ".pdf") return DocumentFamily.Pdf;
        return DocumentFamily.None;
    }

    /// <summary>是不是宏格式（只作源，不作目标）。</summary>
    /// <param name="ext">归一化扩展名</param>
    public static bool IsMacroExtension(string ext) => MacroExtensions.Contains(ext);

    /// <summary>
    /// 生成转换计划。
    /// </summary>
    /// <param name="sourceExt">源（旧）扩展名，归一化小写带点</param>
    /// <param name="targetExt">目标（新）扩展名，归一化小写带点</param>
    /// <param name="allowPdfSource">是否允许把 PDF 当源</param>
    /// <param name="reason">被拒绝时的中文原因（可操作）；成功时为 null</param>
    /// <returns>计划对象；不支持时为 null</returns>
    public static DocumentPlan? Plan(string sourceExt, string targetExt, bool allowPdfSource, out string? reason)
    {
        reason = null;

        var sourceFamily = FamilyOf(sourceExt);
        var targetFamily = FamilyOf(targetExt);
        if (sourceFamily == DocumentFamily.None)
        {
            reason = $"源扩展名 {sourceExt} 不在文档集合内";
            return null;
        }
        if (targetFamily == DocumentFamily.None)
        {
            reason = $"目标扩展名 {targetExt} 不在文档集合内";
            return null;
        }

        // —— 规则 5：宏格式只作源 ——
        if (IsMacroExtension(targetExt))
        {
            reason = $"不支持产出宏格式文件（目标 {targetExt}）；如需去掉宏，请改成 {MacroFreeSuggestion(targetExt)}";
            return null;
        }

        // —— PDF 作源 ——
        if (sourceFamily == DocumentFamily.Pdf)
        {
            if (!allowPdfSource)
            {
                reason = "PDF 作为源默认关闭（allowPdfSource=false）：PDF 是只读版式格式，" +
                         "转出来会丢分栏/浮动对象/表格边界；确需转换请把 allowPdfSource 设为 true（届时只保证文字内容）";
                return null;
            }
            if (targetFamily is not (DocumentFamily.Writer or DocumentFamily.Pdf))
            {
                reason = $"PDF 只能转为文字处理格式或 PDF（目标 {targetExt} 属于 {FamilyName(targetFamily)} 族）";
                return null;
            }
        }
        else if (targetFamily != DocumentFamily.Pdf && targetFamily != sourceFamily)
        {
            // —— 规则 4：非 PDF 目标的跨族转换一律拒绝 ——
            reason = $"不在支持矩阵内：文档跨族转换只允许目标为 pdf（{FamilyName(sourceFamily)} → {FamilyName(targetFamily)}）；" +
                     FamilyHint(targetFamily);
            return null;
        }

        // —— 内置 Markdown 转换器：markdown → docx 不需要任何外置引擎 ——
        if (sourceFamily == DocumentFamily.Writer && IsMarkdown(sourceExt) && targetExt == ".docx")
        {
            return new DocumentPlan
            {
                SourceFamily = sourceFamily,
                TargetFamily = targetFamily,
                TargetExtForFilter = "docx",
                NeedsExternalEngine = false,
                UseBuiltInMarkdown = true,
                Description = "Markdown → Word（内置转换器）",
                LossNote = "内置转换器只输出标题/列表/表格等基本结构，不带 Word 样式与主题"
            };
        }

        // —— 外置引擎过滤表 ——
        var table = sourceFamily switch
        {
            DocumentFamily.Writer => WriterFilters,
            DocumentFamily.Impress => ImpressFilters,
            DocumentFamily.Pdf => PdfSourceFilters,
            _ => CalcFilters
        };
        if (!table.TryGetValue(targetExt, out var filter))
        {
            reason = $"不在支持矩阵内：{sourceExt} → {targetExt} 没有可用的导出过滤器";
            return null;
        }

        return new DocumentPlan
        {
            SourceFamily = sourceFamily,
            TargetFamily = targetFamily,
            TargetExtForFilter = targetExt.TrimStart('.'),
            FilterName = filter,
            FilterOptions = FilterOptionsFor(sourceFamily, targetExt),
            Description = $"{FamilyName(sourceFamily)} 转换（{sourceExt} → {targetExt}）",
            LossNote = LossNoteFor(sourceFamily, sourceExt, targetExt)
        };
    }

    /// <summary>CSV 过滤器选项：44=逗号、34=双引号、76=UTF-8、末位 1=仅导出第一个工作表。</summary>
    private const string CsvOptions = "44,34,76,1,,0,false,true,true,false,false,1";

    /// <summary>TSV 过滤器选项：同 CSV，但分隔符换成 9=制表符。</summary>
    private const string TsvOptions = "9,34,76,1,,0,false,true,true,false,false,1";

    /// <summary>过滤器选项：文本类导出统一要求 UTF-8；TSV 走 CSV 过滤器但分隔符换成制表符。</summary>
    /// <param name="sourceFamily">源文档族</param>
    /// <param name="targetExt">目标扩展名</param>
    private static string? FilterOptionsFor(DocumentFamily sourceFamily, string targetExt) => targetExt switch
    {
        ".csv" => CsvOptions,
        ".tsv" => TsvOptions,
        ".html" or ".htm" or ".md" or ".markdown" => "UTF8",
        _ => null
    };

    /// <summary>有损提示（只在确知会丢内容时提示，不阻断）。</summary>
    /// <param name="sourceFamily">源文档族</param>
    /// <param name="sourceExt">源扩展名</param>
    /// <param name="targetExt">目标扩展名</param>
    private static string? LossNoteFor(DocumentFamily sourceFamily, string sourceExt, string targetExt)
    {
        if (sourceFamily == DocumentFamily.Calc && targetExt is ".csv" or ".tsv")
            return "只导出第一个工作表，公式会变成计算结果";
        if (targetExt is ".md" or ".markdown")
            return "Markdown 输出只保留标题/列表/表格等基本结构";
        if (isTextish(targetExt)) return "图片、边框、页眉页脚等版式信息会丢失";
        if (sourceExt == ".pdf") return "PDF 导入只保证文字内容，版式会明显走样";
        return null;

        static bool isTextish(string ext) => ext is ".txt" or ".html" or ".htm" or ".xhtml";
    }

    /// <summary>去掉宏的目标建议：docm → docx，xlsm → xlsx，pptm → pptx。</summary>
    /// <param name="macroExt">宏格式扩展名</param>
    private static string MacroFreeSuggestion(string macroExt) => macroExt switch
    {
        ".docm" => ".docx",
        ".xlsm" => ".xlsx",
        ".pptm" => ".pptx",
        _ => ".docx"
    };

    /// <summary>是不是 Markdown 扩展名。</summary>
    /// <param name="ext">归一化扩展名</param>
    private static bool IsMarkdown(string ext) => ext is ".md" or ".markdown";

    /// <summary>文档族的中文名（写日志用）。</summary>
    /// <param name="family">文档族</param>
    public static string FamilyName(DocumentFamily family) => family switch
    {
        DocumentFamily.Writer => "文字处理",
        DocumentFamily.Impress => "演示文稿",
        DocumentFamily.Calc => "电子表格",
        DocumentFamily.Pdf => "PDF",
        _ => "未知"
    };

    /// <summary>目标族的可操作建议（拒绝跨族时追加在原因后面）。</summary>
    /// <param name="targetFamily">目标文档族</param>
    private static string FamilyHint(DocumentFamily targetFamily) => targetFamily switch
    {
        DocumentFamily.Calc => "表格请改成 csv/pdf",
        DocumentFamily.Impress => "演示文稿请改成 pdf",
        DocumentFamily.Writer => "文档请改成 pdf",
        _ => "请改成 pdf"
    };
}
