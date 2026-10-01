using System.IO.Compression;
using System.Text;
using RenamePro.Core;

namespace RenamePro.Conversion;

/// <summary>
/// 内置 Markdown → Word(.docx) 转换器：不依赖任何外置引擎（LibreOffice / Office 都不需要），
/// 直接用 <see cref="ZipArchive"/> 按 ECMA-376 最小包结构写出 WordprocessingML。
///
/// 为什么手写而不是引第三方包：项目约束是"只允许 Magick.NET 一个第三方 NuGet 包"，
/// 而 .docx 本质就是一个 ZIP + 若干 XML，最小可打开包只需要 4 个部件，
/// 手写反而比引入 Open XML SDK（数 MB 依赖）更符合本项目的体积取向。
///
/// 支持：ATX 标题（# ~ ######）、粗体/斜体/行内代码、围栏代码块、有序/无序列表、
///       引用块、水平线、链接、图片（嵌入为关系图）、表格（含表头）、段落与硬换行。
/// 明确不支持（写进 README 已知限制）：Word 样式与主题、脚注、引用式链接、嵌套列表缩进、
///       原始 HTML 块——不支持的语法按纯文本落进文档，不做静默丢弃。
/// </summary>
public static class MarkdownToDocx
{
    /// <summary>正文默认字号（半磅）：21 half-points = 10.5pt = 五号。</summary>
    private const string DefaultFontSizeHalfPoints = "21";

    /// <summary>正文字体：中文用等线，西文用 Calibri（Word 会自动做 eastAsia 回退）。</summary>
    private const string BodyFont = "Calibri";
    private const string EastAsiaFont = "等线";

    /// <summary>
    /// 把 Markdown 文本转换为 docx 文件。
    /// </summary>
    /// <param name="markdown">Markdown 源文本</param>
    /// <param name="targetPath">目标 docx 路径（调用方给的是临时文件路径）</param>
    /// <param name="sourceDirectory">源文件所在目录（图片相对路径按它解析）</param>
    public static void Convert(string markdown, string targetPath, string sourceDirectory)
    {
        var blocks = ParseBlocks(markdown);

        // 图片按出现顺序建立关系（rId2 起，rId1 留给 styles）
        var images = new List<string>();
        for (var i = 0; i < blocks.Count; i++)
        {
            if (blocks[i] is not ImageBlock image) continue;
            var resolved = ResolveImagePath(image.Target, sourceDirectory);
            if (resolved == null)
            {
                // 图片找不到：退化成"图片说明文字"，避免产出断链的 docx
                blocks[i] = new ParagraphBlock($"［图片未找到：{image.Alt}］");
                continue;
            }
            images.Add(resolved);
            blocks[i] = image with { ResolvedPath = resolved, RelationshipId = $"rId{images.Count + 1}" };
        }

        // 必须显式关掉 ZipArchive 与文件流再返回：ZIP 的中央目录是在 Dispose 时才写入的，
        // 而调用方（DocConverter）紧接着要删除工作目录——句柄没释放会删不掉，留下垃圾目录。
        using (var stream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "[Content_Types].xml", ContentTypesXml(images.Count > 0));
            WriteEntry(zip, "_rels/.rels", RootRelsXml());
            WriteEntry(zip, "word/_rels/document.xml.rels", DocumentRelsXml(images));
            WriteEntry(zip, "word/styles.xml", StylesXml());
            WriteEntry(zip, "word/document.xml", DocumentXml(blocks, images));
            for (var i = 0; i < images.Count; i++)
            {
                WriteBinaryEntry(zip, $"word/media/image{i + 1}{ExtensionOf(images[i])}", images[i]);
            }
        }
    }

    /// <summary>不依赖引擎的可用性判断：内置转换器永远可用（只要目标目录可写）。</summary>
    public static bool IsAvailable => true;

    // ------------------------------------------------------------------ 包部件

    /// <summary>[Content_Types].xml：声明各部件的内容类型（图片按扩展名声明默认类型）。</summary>
    private static string ContentTypesXml(bool hasImages)
    {
        var builder = new StringBuilder();
        builder.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        builder.Append("""<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">""");
        builder.Append("""<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>""");
        builder.Append("""<Default Extension="xml" ContentType="application/xml"/>""");
        if (hasImages)
        {
            builder.Append("""<Default Extension="png" ContentType="image/png"/>""");
            builder.Append("""<Default Extension="jpg" ContentType="image/jpeg"/>""");
            builder.Append("""<Default Extension="jpeg" ContentType="image/jpeg"/>""");
            builder.Append("""<Default Extension="gif" ContentType="image/gif"/>""");
            builder.Append("""<Default Extension="bmp" ContentType="image/bmp"/>""");
            builder.Append("""<Default Extension="webp" ContentType="image/webp"/>""");
        }
        builder.Append("""<Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>""");
        builder.Append("""<Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>""");
        builder.Append("</Types>");
        return builder.ToString();
    }

    /// <summary>包级关系：主文档部件。</summary>
    private static string RootRelsXml() =>
        """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>"""
        + """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">"""
        + """<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>"""
        + "</Relationships>";

    /// <summary>主文档的关系：样式 + 图片（图片关系 ID 由调用方分配，与正文里的 r:embed 对应）。</summary>
    private static string DocumentRelsXml(List<string> images)
    {
        var builder = new StringBuilder();
        builder.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        builder.Append("""<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""");
        builder.Append("""<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>""");
        for (var i = 0; i < images.Count; i++)
        {
            builder.Append($"""<Relationship Id="rId{i + 2}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="media/image{i + 1}{ExtensionOf(images[i])}"/>""");
        }
        builder.Append("</Relationships>");
        return builder.ToString();
    }

    /// <summary>样式表：标题 1~6、正文、代码块、引用块、表格文本、列表段落。</summary>
    private static string StylesXml()
    {
        var builder = new StringBuilder();
        builder.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        builder.Append("""<w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">""");
        // 文档默认：正文样式
        builder.Append("<w:docDefaults><w:rPrDefault><w:rPr>");
        builder.Append($"<w:rFonts w:ascii=\"{BodyFont}\" w:hAnsi=\"{BodyFont}\" w:eastAsia=\"{EastAsiaFont}\"/>");
        builder.Append($"<w:sz w:val=\"{DefaultFontSizeHalfPoints}\"/><w:szCs w:val=\"{DefaultFontSizeHalfPoints}\"/>");
        builder.Append("</w:rPr></w:rPrDefault><w:pPrDefault><w:pPr>");
        builder.Append("""<w:spacing w:after="120" w:line="276" w:lineRule="auto"/>""");
        builder.Append("</w:pPr></w:pPrDefault></w:docDefaults>");
        builder.Append(NormalStyle());
        builder.Append(HeadingStyles());
        builder.Append("""<w:style w:type="paragraph" w:styleId="CodeBlock"><w:name w:val="Code Block"/><w:basedOn w:val="Normal"/><w:pPr><w:shd w:val="clear" w:color="auto" w:fill="F5F5F5"/><w:spacing w:before="60" w:after="120"/></w:pPr><w:rPr><w:rFonts w:ascii="Consolas" w:hAnsi="Consolas" w:eastAsia="Consolas"/><w:sz w:val="19"/></w:rPr></w:style>""");
        builder.Append("""<w:style w:type="paragraph" w:styleId="QuoteBlock"><w:name w:val="Quote Block"/><w:basedOn w:val="Normal"/><w:pPr><w:ind w:left="480"/><w:pBdr><w:left w:val="single" w:sz="18" w:space="8" w:color="BFBFBF"/></w:pBdr></w:pPr><w:rPr><w:i/><w:color w:val="595959"/></w:rPr></w:style>""");
        builder.Append("""<w:style w:type="paragraph" w:styleId="ListParagraph"><w:name w:val="List Paragraph"/><w:basedOn w:val="Normal"/><w:pPr><w:spacing w:after="40"/></w:pPr></w:style>""");
        builder.Append("""<w:style w:type="character" w:styleId="CodeChar"><w:name w:val="Code Char"/><w:rPr><w:rFonts w:ascii="Consolas" w:hAnsi="Consolas" w:eastAsia="Consolas"/><w:shd w:val="clear" w:color="auto" w:fill="F2F2F2"/></w:rPr></w:style>""");
        builder.Append("</w:styles>");
        return builder.ToString();
    }

    /// <summary>正文样式（docDefaults 已给字体，这里只固定样式 ID，供 Heading 继承）。</summary>
    private static string NormalStyle() =>
        """<w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:qFormat/></w:style>""";

    /// <summary>标题 1~6 样式（字号依次递减，加粗，段前段后留白）。</summary>
    private static string HeadingStyles()
    {
        var builder = new StringBuilder();
        string[] sizes = { "40", "32", "28", "24", "22", "21" };
        for (var level = 1; level <= 6; level++)
        {
            var before = level == 1 ? 240 : 200;
            builder.Append($"""<w:style w:type="paragraph" w:styleId="Heading{level}"><w:name w:val="heading {level}"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/><w:pPr><w:keepNext/><w:outlineLvl w:val="{level - 1}"/><w:spacing w:before="{before}" w:after="120"/></w:pPr><w:rPr><w:b/><w:sz w:val="{sizes[level - 1]}"/><w:szCs w:val="{sizes[level - 1]}"/></w:rPr></w:style>""");
        }
        return builder.ToString();
    }

    /// <summary>正文部件：按块顺序输出段落/表格。</summary>
    private static string DocumentXml(List<Block> blocks, List<string> images)
    {
        var builder = new StringBuilder();
        builder.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        builder.Append("""<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" """);
        builder.Append("""xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" """);
        builder.Append("""xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" """);
        builder.Append("""xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" """);
        builder.Append("""xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture">""");
        builder.Append("<w:body>");
        foreach (var block in blocks) builder.Append(RenderBlock(block));
        // 结尾必须有一个 sectPr，否则 Word 会认为文档损坏
        builder.Append("""<w:sectPr><w:pgSz w:w="11906" w:h="16838"/><w:pgMar w:top="1440" w:right="1440" w:bottom="1440" w:left="1440" w:header="851" w:footer="992" w:gutter="0"/></w:sectPr>""");
        builder.Append("</w:body></w:document>");
        return builder.ToString();
    }

    /// <summary>把单个块渲染成 WordprocessingML。</summary>
    private static string RenderBlock(Block block) => block switch
    {
        HeadingBlock heading => Paragraph($"Heading{Math.Clamp(heading.Level, 1, 6)}", RenderInline(heading.Runs)),
        ParagraphBlock paragraph => Paragraph("Normal", RenderInline(paragraph.Runs)),
        QuoteBlock quote => Paragraph("QuoteBlock", RenderInline(quote.Runs)),
        CodeBlock code => CodeParagraphs(code),
        ListBlock list => ListParagraphs(list),
        TableBlock table => Table(table),
        ImageBlock image => ImageParagraph(image),
        HorizontalRuleBlock => """<w:p><w:pPr><w:pBdr><w:bottom w:val="single" w:sz="6" w:space="1" w:color="A6A6A6"/></w:pBdr></w:pPr></w:p>""",
        _ => string.Empty
    };

    /// <summary>普通段落。</summary>
    private static string Paragraph(string styleId, string runs) =>
        $"""<w:p><w:pPr><w:pStyle w:val="{styleId}"/></w:pPr>{runs}</w:p>""";

    /// <summary>代码块：逐行段落 + 等宽字体 + 浅灰底，保留行首空格（w:t 的 xml:space）。</summary>
    private static string CodeParagraphs(CodeBlock code)
    {
        var builder = new StringBuilder();
        var lines = code.Text.Replace("\r\n", "\n").Split('\n');
        foreach (var line in lines)
        {
            // 空行给一个空格：否则 Word 里代码块的空行会被压掉，行号语义丢失
            var text = line.Length == 0 ? " " : line;
            builder.Append("""<w:p><w:pPr><w:pStyle w:val="CodeBlock"/></w:pPr><w:r>""");
            builder.Append($"""<w:t xml:space="preserve">{Escape(text)}</w:t>""");
            builder.Append("</w:r></w:p>");
        }
        if (lines.Length == 0) builder.Append(Paragraph("CodeBlock", string.Empty));
        return builder.ToString();
    }

    /// <summary>列表：无序用 "•"，有序用序号文本（不引入 numbering.xml，避免编号重启的复杂度）。</summary>
    private static string ListParagraphs(ListBlock list)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < list.Items.Count; i++)
        {
            var marker = list.Ordered ? $"{i + 1}. " : "• ";
            var indent = 360 + list.Depth * 360;
            builder.Append($"""<w:p><w:pPr><w:pStyle w:val="ListParagraph"/><w:ind w:left="{indent}" w:hanging="360"/></w:pPr>""");
            builder.Append($"""<w:r><w:t xml:space="preserve">{Escape(marker)}</w:t></w:r>""");
            builder.Append(RenderInline(list.Items[i]));
            builder.Append("</w:p>");
        }
        return builder.ToString();
    }

    /// <summary>表格：首行作表头（加粗 + 灰底），其余为数据行。</summary>
    private static string Table(TableBlock table)
    {
        var builder = new StringBuilder();
        builder.Append("""<w:tbl><w:tblPr><w:tblStyle w:val="TableGrid"/><w:tblW w:w="0" w:type="auto"/>""");
        builder.Append("""<w:tblBorders><w:top w:val="single" w:sz="4" w:space="0" w:color="A6A6A6"/><w:left w:val="single" w:sz="4" w:space="0" w:color="A6A6A6"/><w:bottom w:val="single" w:sz="4" w:space="0" w:color="A6A6A6"/><w:right w:val="single" w:sz="4" w:space="0" w:color="A6A6A6"/><w:insideH w:val="single" w:sz="4" w:space="0" w:color="A6A6A6"/><w:insideV w:val="single" w:sz="4" w:space="0" w:color="A6A6A6"/></w:tblBorders>""");
        builder.Append("</w:tblPr><w:tblGrid/>");
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var isHeader = r == 0;
            builder.Append("<w:tr>");
            if (isHeader) builder.Append("<w:trPr><w:tblHeader/></w:trPr>");
            foreach (var cell in table.Rows[r])
            {
                builder.Append("<w:tc><w:tcPr>");
                if (isHeader) builder.Append("""<w:shd w:val="clear" w:color="auto" w:fill="F2F2F2"/>""");
                builder.Append("</w:tcPr>");
                builder.Append("""<w:p><w:pPr><w:spacing w:after="0"/></w:pPr>""");
                if (isHeader) builder.Append("<w:r><w:rPr><w:b/></w:rPr>");
                else builder.Append("<w:r>");
                builder.Append($"""<w:t xml:space="preserve">{Escape(cell)}</w:t></w:r></w:p></w:tc>""");
            }
            builder.Append("</w:tr>");
        }
        builder.Append("</w:tbl>");
        // 表格后补一个空段落：Word 里两个相邻表格/表格贴段落需要段落分隔
        builder.Append("""<w:p><w:pPr><w:spacing w:after="0"/></w:pPr></w:p>""");
        return builder.ToString();
    }

    /// <summary>图片段落：按 6 英寸宽等比缩放（高度按常见 4:3 估），带说明文字。</summary>
    private static string ImageParagraph(ImageBlock image)
    {
        const int widthEmu = 5486400; // 6 inch = 6 * 914400 EMU
        const int heightEmu = 3657600; // 4 inch
        var builder = new StringBuilder();
        builder.Append("<w:p><w:pPr><w:jc w:val=\"center\"/></w:pPr><w:r><w:drawing>");
        builder.Append($"""<wp:inline distT="0" distB="0" distL="0" distR="0"><wp:extent cx="{widthEmu}" cy="{heightEmu}"/><wp:docPr id="{Math.Abs(image.RelationshipId.GetHashCode() % 100000) + 1}" name="Picture" descr="{Escape(image.Alt)}"/>""");
        builder.Append("""<a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic>""");
        builder.Append($"""<pic:nvPicPr><pic:cNvPr id="0" name="{Escape(image.Alt)}"/><pic:cNvPicPr/></pic:nvPicPr>""");
        builder.Append($"""<pic:blipFill><a:blip r:embed="{image.RelationshipId}"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>""");
        builder.Append($"""<pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="{widthEmu}" cy="{heightEmu}"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr>""");
        builder.Append("</pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>");
        return builder.ToString();
    }

    /// <summary>行内元素渲染：粗体/斜体/行内代码/链接/普通文本（嵌套按最内层标记处理）。</summary>
    private static string RenderInline(List<InlineRun> runs)
    {
        var builder = new StringBuilder();
        foreach (var run in runs)
        {
            var rPr = new StringBuilder();
            if (run.Bold) rPr.Append("<w:b/>");
            if (run.Italic) rPr.Append("<w:i/>");
            if (run.Code) rPr.Append("""<w:rStyle w:val="CodeChar"/>""");
            if (run.Link != null) rPr.Append("""<w:color w:val="0563C1"/><w:u w:val="single"/>""");

            var text = run.Text;
            if (run.Link != null && run.Text.Length == 0) text = run.Link;
            if (text.Length == 0) continue;

            builder.Append("<w:r>");
            if (rPr.Length > 0) builder.Append($"<w:rPr>{rPr}</w:rPr>");
            builder.Append($"""<w:t xml:space="preserve">{Escape(text)}</w:t></w:r>""");
        }
        // 空段落：Word 需要一个显式空 run 才会显示成空行（否则是零高度）
        if (builder.Length == 0) builder.Append("<w:r><w:t xml:space=\"preserve\"> </w:t></w:r>");
        return builder.ToString();
    }

    // ------------------------------------------------------------------ 解析

    /// <summary>把 Markdown 拆成块级元素。</summary>
    /// <param name="markdown">Markdown 文本</param>
    private static List<Block> ParseBlocks(string markdown)
    {
        var blocks = new List<Block>();
        // 去掉 BOM：带 BOM 的文件（记事本、部分导出工具）第一行会变成 "\uFEFF# 标题"，
        // 不清掉的话标题会被当成普通段落。
        var normalized = markdown.TrimStart('\uFEFF');
        var lines = normalized.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var paragraph = new List<string>();
        var i = 0;

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            // 段落内的单个换行按"硬换行"处理：合并成一行文本（Markdown 语义）
            var text = string.Join(" ", paragraph).Trim();
            if (text.Length > 0) blocks.Add(new ParagraphBlock(ParseInline(text)));
            paragraph.Clear();
        }

        while (i < lines.Length)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            // 围栏代码块
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                FlushParagraph();
                var fence = trimmed[..3];
                i++;
                var code = new List<string>();
                while (i < lines.Length && !lines[i].TrimStart().StartsWith(fence, StringComparison.Ordinal))
                {
                    code.Add(lines[i]);
                    i++;
                }
                i++; // 跳过收尾围栏
                blocks.Add(new CodeBlock(string.Join("\n", code)));
                continue;
            }

            // 水平线
            if (IsHorizontalRule(trimmed))
            {
                FlushParagraph();
                blocks.Add(new HorizontalRuleBlock());
                i++;
                continue;
            }

            // ATX 标题
            var headingLevel = HeadingLevel(trimmed);
            if (headingLevel > 0)
            {
                FlushParagraph();
                var content = trimmed[headingLevel..].Trim().TrimEnd('#').Trim();
                blocks.Add(new HeadingBlock(headingLevel, ParseInline(content)));
                i++;
                continue;
            }

            // 表格：当前行含 |，下一行是分隔行
            if (trimmed.Contains('|') && i + 1 < lines.Length && IsTableSeparator(lines[i + 1].TrimStart()))
            {
                FlushParagraph();
                var (table, next) = ParseTable(lines, i);
                blocks.Add(table);
                i = next;
                continue;
            }

            // 引用块：连续 > 行合并
            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                var quote = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('>'))
                {
                    quote.Add(lines[i].TrimStart()[1..].TrimStart());
                    i++;
                }
                blocks.Add(new QuoteBlock(ParseInline(string.Join(" ", quote))));
                continue;
            }

            // 列表：连续同类型列表项合并为一个块（不同缩进层级分别成块）
            if (IsListItem(trimmed, out var ordered, out var depth, out var itemText))
            {
                FlushParagraph();
                var items = new List<List<InlineRun>>();
                var blockOrdered = ordered;
                var blockDepth = depth;
                while (i < lines.Length && IsListItem(lines[i].TrimStart(), out var o2, out var d2, out var t2)
                       && o2 == blockOrdered && d2 == blockDepth)
                {
                    items.Add(ParseInline(t2));
                    i++;
                }
                blocks.Add(new ListBlock(blockOrdered, blockDepth, items));
                continue;
            }

            // 独立成行的图片
            var standaloneImage = ParseStandaloneImage(trimmed);
            if (standaloneImage != null)
            {
                FlushParagraph();
                blocks.Add(standaloneImage);
                i++;
                continue;
            }

            // 空行结束段落
            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                i++;
                continue;
            }

            paragraph.Add(line.Trim());
            i++;
        }
        FlushParagraph();
        return blocks;
    }

    /// <summary>解析表格（含表头行 + 分隔行 + 数据行）。返回解析到的下一行下标。</summary>
    private static (TableBlock Table, int Next) ParseTable(string[] lines, int start)
    {
        var rows = new List<List<string>>();
        var header = SplitTableRow(lines[start]);
        rows.Add(header);
        var i = start + 2; // 跳过分隔行
        while (i < lines.Length)
        {
            var line = lines[i].TrimStart();
            if (!line.Contains('|') || string.IsNullOrWhiteSpace(line)) break;
            rows.Add(SplitTableRow(lines[i]));
            i++;
        }
        // 列数对齐到表头列数（多截少补），避免 Word 里出现锯齿状表格
        foreach (var row in rows)
        {
            while (row.Count < header.Count) row.Add(string.Empty);
            if (row.Count > header.Count) row.RemoveRange(header.Count, row.Count - header.Count);
        }
        return (new TableBlock(rows), i);
    }

    /// <summary>按 | 拆分表格行，去掉首尾空单元格（Markdown 允许行首尾都有 |）。</summary>
    private static List<string> SplitTableRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|')) trimmed = trimmed[1..];
        if (trimmed.EndsWith('|')) trimmed = trimmed[..^1];
        return trimmed.Split('|').Select(c => c.Trim()).ToList();
    }

    /// <summary>分隔行判定：| --- | :--: | 这类。</summary>
    private static bool IsTableSeparator(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return false;
        var cells = SplitTableRow(trimmed);
        if (cells.Count == 0) return false;
        foreach (var cell in cells)
        {
            var c = cell.Trim(':').Trim();
            if (c.Length == 0) return false;
            foreach (var ch in c)
            {
                if (ch != '-') return false;
            }
        }
        return true;
    }

    /// <summary>标题层级：1~6，非标题返回 0。</summary>
    private static int HeadingLevel(string trimmed)
    {
        var level = 0;
        while (level < trimmed.Length && trimmed[level] == '#') level++;
        if (level is < 1 or > 6) return 0;
        // # 之后必须是空格或行尾，否则是 #标签
        if (level < trimmed.Length && trimmed[level] != ' ') return 0;
        return level;
    }

    /// <summary>水平线判定。</summary>
    private static bool IsHorizontalRule(string trimmed)
    {
        var stripped = trimmed.Replace(" ", string.Empty);
        if (stripped.Length < 3) return false;
        return stripped.All(c => c == '-') || stripped.All(c => c == '*') || stripped.All(c => c == '_');
    }

    /// <summary>列表项判定：返回是否有序、缩进层级（按前导空格/4 计算）与内容。</summary>
    private static bool IsListItem(string trimmed, out bool ordered, out int depth, out string text)
    {
        ordered = false;
        depth = 0;
        text = string.Empty;
        var indent = 0;
        while (indent < trimmed.Length && trimmed[indent] == ' ') indent++;
        if (indent >= trimmed.Length) return false;
        var rest = trimmed[indent..];

        if (rest.StartsWith("- ", StringComparison.Ordinal) || rest.StartsWith("* ", StringComparison.Ordinal)
            || rest.StartsWith("+ ", StringComparison.Ordinal))
        {
            ordered = false;
            depth = indent / 2;
            text = rest[2..].Trim();
            return true;
        }
        var digits = 0;
        while (digits < rest.Length && char.IsDigit(rest[digits])) digits++;
        if (digits > 0 && digits + 1 < rest.Length && (rest[digits] == '.' || rest[digits] == ')') && rest[digits + 1] == ' ')
        {
            ordered = true;
            depth = indent / 2;
            text = rest[(digits + 2)..].Trim();
            return true;
        }
        return false;
    }

    /// <summary>独立成行的图片 ![alt](path)；行内图片按链接文本处理。</summary>
    private static ImageBlock? ParseStandaloneImage(string trimmed)
    {
        if (!trimmed.StartsWith("![", StringComparison.Ordinal)) return null;
        var close = trimmed.IndexOf("](", StringComparison.Ordinal);
        if (close < 0 || !trimmed.EndsWith(')')) return null;
        var alt = trimmed[2..close];
        var target = trimmed[(close + 2)..^1].Trim();
        if (target.Length == 0) return null;
        return new ImageBlock(alt, target);
    }

    /// <summary>
    /// 行内解析：粗体、斜体、行内代码、链接。刻意保持简单（单次扫描 + 配对查找），
    /// 不实现引用式链接与嵌套强调——它们会按纯文本原样输出，不做静默丢弃。
    /// 每个 run 的格式由"发现标记时"决定，因此所有分支都必须显式带上格式标记。
    /// </summary>
    private static List<InlineRun> ParseInline(string text)
    {
        var runs = new List<InlineRun>();
        var buffer = new StringBuilder();

        void Emit(bool bold, bool italic, bool code, string? link)
        {
            if (buffer.Length == 0) return;
            runs.Add(new InlineRun(buffer.ToString(), bold, italic, code, link));
            buffer.Clear();
        }

        void Plain() => Emit(false, false, false, null);

        var i = 0;
        while (i < text.Length)
        {
            // 行内代码：`code`
            if (text[i] == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    Plain();
                    runs.Add(new InlineRun(text[(i + 1)..end], false, false, true, null));
                    i = end + 1;
                    continue;
                }
            }
            // 图片（行内）：退化为链接文本
            if (text[i] == '!' && i + 1 < text.Length && text[i + 1] == '[')
            {
                var closeAlt = text.IndexOf("](", i + 2, StringComparison.Ordinal);
                if (closeAlt > 0)
                {
                    var closeParen = text.IndexOf(')', closeAlt + 2);
                    if (closeParen > 0)
                    {
                        Plain();
                        var alt = text[(i + 2)..closeAlt];
                        var target = text[(closeAlt + 2)..closeParen];
                        runs.Add(new InlineRun(alt.Length > 0 ? alt : target, false, false, false, target));
                        i = closeParen + 1;
                        continue;
                    }
                }
            }
            // 链接：[text](url)
            if (text[i] == '[')
            {
                var closeText = text.IndexOf("](", i + 1, StringComparison.Ordinal);
                if (closeText > 0)
                {
                    var closeParen = text.IndexOf(')', closeText + 2);
                    if (closeParen > 0)
                    {
                        Plain();
                        var label = text[(i + 1)..closeText];
                        var target = text[(closeText + 2)..closeParen];
                        runs.Add(new InlineRun(label.Length > 0 ? label : target, false, false, false, target));
                        i = closeParen + 1;
                        continue;
                    }
                }
            }
            // 粗体：**text** 与 __text__
            if (i + 1 < text.Length && ((text[i] == '*' && text[i + 1] == '*') || (text[i] == '_' && text[i + 1] == '_')))
            {
                var marker = text.Substring(i, 2);
                var end = text.IndexOf(marker, i + 2, StringComparison.Ordinal);
                if (end > i)
                {
                    Plain();
                    runs.Add(new InlineRun(text[(i + 2)..end], true, false, false, null));
                    i = end + 2;
                    continue;
                }
            }
            // 斜体：*text* 与 _text_
            if (text[i] is '*' or '_')
            {
                var end = text.IndexOf(text[i], i + 1);
                if (end > i + 1)
                {
                    Plain();
                    runs.Add(new InlineRun(text[(i + 1)..end], false, true, false, null));
                    i = end + 1;
                    continue;
                }
            }
            buffer.Append(text[i]);
            i++;
        }
        Plain();
        return runs;
    }

    // ------------------------------------------------------------------ 工具

    /// <summary>图片路径解析：支持相对路径与绝对路径，找不到返回 null。</summary>
    private static string? ResolveImagePath(string target, string sourceDirectory)
    {
        try
        {
            if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
            var path = Path.IsPathRooted(target) ? target : Path.Combine(sourceDirectory, target);
            path = Uri.UnescapeDataString(path);
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>图片扩展名（用于 media 文件名与内容类型）。</summary>
    private static string ExtensionOf(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" => ext,
            _ => ".png" // 内容类型只声明了这几种；未知扩展名按 png 声明，Word 仍能按字节识别
        };
    }

    /// <summary>写入 UTF-8（无 BOM）文本部件。</summary>
    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    /// <summary>写入二进制部件（图片）。</summary>
    private static void WriteBinaryEntry(ZipArchive zip, string name, string sourcePath)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression); // 图片已压缩，再压没意义
        using var target = entry.Open();
        using var source = File.OpenRead(sourcePath);
        source.CopyTo(target);
    }

    /// <summary>XML 文本转义（&amp; &lt; &gt; 引号），并剔除 XML 1.0 不允许的控制字符。</summary>
    private static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length + 16);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '&': builder.Append("&amp;"); break;
                case '<': builder.Append("&lt;"); break;
                case '>': builder.Append("&gt;"); break;
                case '"': builder.Append("&quot;"); break;
                case '\'': builder.Append("&apos;"); break;
                case '\t': builder.Append("&#9;"); break;
                default:
                    // XML 1.0 合法字符：Tab/CR/LF 与 >=0x20；其余（含代理对以外的控制字符）丢弃
                    if (ch >= 0x20 || ch is '\n' or '\r') builder.Append(ch);
                    break;
            }
        }
        return builder.ToString();
    }

    // ------------------------------------------------------------------ 块模型

    /// <summary>块级元素基类。</summary>
    private abstract record Block;

    /// <summary>标题块。</summary>
    private sealed record HeadingBlock(int Level, List<InlineRun> Runs) : Block;

    /// <summary>普通段落。</summary>
    private sealed record ParagraphBlock(List<InlineRun> Runs) : Block
    {
        /// <summary>由纯文本构造（图片找不到时的降级路径）。</summary>
        public ParagraphBlock(string text) : this(new List<InlineRun> { new(text, false, false, false, null) })
        {
        }
    }

    /// <summary>引用块。</summary>
    private sealed record QuoteBlock(List<InlineRun> Runs) : Block;

    /// <summary>代码块（原样保留）。</summary>
    private sealed record CodeBlock(string Text) : Block;

    /// <summary>列表块。</summary>
    private sealed record ListBlock(bool Ordered, int Depth, List<List<InlineRun>> Items) : Block;

    /// <summary>表格块（首行为表头）。</summary>
    private sealed record TableBlock(List<List<string>> Rows) : Block;

    /// <summary>图片块（关系 ID 与落盘路径在写包前补齐）。</summary>
    private sealed record ImageBlock(string Alt, string Target, string? ResolvedPath = null, string RelationshipId = "")
        : Block;

    /// <summary>水平线。</summary>
    private sealed record HorizontalRuleBlock : Block;

    /// <summary>行内片段。</summary>
    private sealed record InlineRun(string Text, bool Bold, bool Italic, bool Code, string? Link);
}
