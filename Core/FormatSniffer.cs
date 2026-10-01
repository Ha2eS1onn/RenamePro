using System.IO.Compression;
using System.Text;

namespace RenamePro.Core;

/// <summary>
/// 文件格式嗅探器：读取文件头 magic number 识别真实格式，
/// 并校验真实格式是否与源（改名前的）扩展名一致——统一前置校验，不一致的任务直接跳过。
/// 同族格式互认（mp4/mov/m4a 同为 isobmff，mkv/webm 同为 ebml）。
///
/// 文档族的判定分两层：
///   1. 固定签名：OLE2（doc/xls/ppt）、RTF、PDF 直接看文件头前几个字节；
///   2. ZIP 容器（docx/pptx/xlsx/odt/ods/odp 全是 ZIP）：**不能只看文件头**。
///      ZIP 的条目名在"中央目录"里，而中央目录位于文件末尾；本地文件头里的名字长度不定，
///      压缩数据也不展开，因此靠前 64 字节猜条目名在真实 Office 文档上必然误判。
///      正确做法是用 <see cref="ZipArchive"/> 打真实条目列表，按"首个条目 + 主部件路径"分流：
///        OOXML 规范要求 [Content_Types].xml 是条目之一，主部件在 word/ 、ppt/ 、xl/ 下；
///        ODF 要求第一个条目是（stored 方式存储的）mimetype，内含完整 MIME 字符串。
///      认不出具体文档类型时返回 "zip"（普通压缩包），它在所有文档扩展名的期望集合里都不存在，
///      因此会被判为"magic 不符"而跳过——失败方向始终安全。
///
/// 纯文本族（md / markdown / csv / html）没有 magic number 可用，走"负证据"校验：
/// 只有"任何已知二进制格式都不匹配"才认为它是文本（strict:false 重载），
/// 一旦认出二进制签名就拒绝——保证"宁可漏转换，也不错转换"。
/// </summary>
public static class FormatSniffer
{
    /// <summary>
    /// 文件头读取长度。必须 ≥ 0x30 + 32 = 48 字节，否则 OLE2 的根目录条目名读不全
    /// （读不全就只能退回"头窗口里找特征名"的兜底路径，宏文档场景会误判）；
    /// 留 96 字节也给 gzip/HTML 之类的文本探测留了余量。
    /// </summary>
    private const int HeaderSize = 96;

    /// <summary>
    /// 嗅探文件格式并返回归一化格式标识（如 jpeg / png / isobmff / ooxml-word）；无法识别返回 null。
    /// </summary>
    /// <param name="filePath">文件完整路径（直接使用 path 重载，兼容中文与空格）</param>
    /// <returns>格式标识或 null</returns>
    public static string? Sniff(string filePath)
    {
        var header = new byte[HeaderSize];
        int read;
        using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            read = stream.Read(header, 0, header.Length);
        }
        if (read == 0) return null; // 空文件：认不出（1~3 字节的文本文件仍要走放宽校验，因此不能用 read < 4 早退）

        // —— 按特征逐个判断（定长特征优先）——
        if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) return "jpeg";
        if (read >= 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47
            && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A) return "png";
        if (header[0] == 'B' && header[1] == 'M') return "bmp";
        if (read >= 6 && header[0] == 'G' && header[1] == 'I' && header[2] == 'F' && header[3] == '8'
            && (header[4] == '7' || header[4] == '9') && header[5] == 'a') return "gif";
        if (read >= 12 && header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F')
        {
            // RIFF 家族：用 8~12 字节区分 WEBP / AVI / WAVE
            var form = Encoding.ASCII.GetString(header, 8, 4);
            return form switch
            {
                "WEBP" => "webp",
                "AVI " => "riff-avi",
                "WAVE" => "riff-wav",
                _ => null
            };
        }
        if ((header[0] == 'I' && header[1] == 'I' && header[2] == 0x2A && header[3] == 0x00)
            || (header[0] == 'M' && header[1] == 'M' && header[2] == 0x00 && header[3] == 0x2A)) return "tiff";
        if (header[0] == 0x00 && header[1] == 0x00 && header[2] == 0x01 && header[3] == 0x00) return "ico";
        if (read >= 8 && header[4] == 'f' && header[5] == 't' && header[6] == 'y' && header[7] == 'p') return "isobmff"; // mp4/mov/m4a
        if (header[0] == 0x1A && header[1] == 0x45 && header[2] == 0xDF && header[3] == 0xA3) return "ebml"; // mkv/webm
        if (read >= 16 && IsAsfGuid(header)) return "asf"; // wmv
        if (header[0] == 'f' && header[1] == 'L' && header[2] == 'a' && header[3] == 'C') return "flac";
        if (header[0] == 'O' && header[1] == 'g' && header[2] == 'g' && header[3] == 'S') return "ogg";
        if (header[0] == 'I' && header[1] == 'D' && header[2] == '3') return "mp3"; // ID3v2 标签开头
        if (header[0] == 'A' && header[1] == 'D' && header[2] == 'I' && header[3] == 'F') return "adif"; // aac
        if (header[0] == 0x47) return "mpegts"; // ts 的同步字节

        // —— 文档族：固定签名的三种 ——
        // OLE2 复合文档（doc / xls / ppt 共用同一签名，靠偏移 0x30 的根目录条目名区分）
        if (read >= 8 && header[0] == 0xD0 && header[1] == 0xCF && header[2] == 0x11 && header[3] == 0xE0
            && header[4] == 0xA1 && header[5] == 0xB1 && header[6] == 0x1A && header[7] == 0xE1)
            return SniffOle2(header, read);
        if (read >= 5 && header[0] == '{' && header[1] == '\\' && header[2] == 'r' && header[3] == 't' && header[4] == 'f') return "rtf";
        if (read >= 5 && header[0] == '%' && header[1] == 'P' && header[2] == 'D' && header[3] == 'F' && header[4] == '-') return "pdf";

        // —— 文档族：ZIP 容器（必须读中央目录，见类注释）——
        if (IsZipSignature(header, read)) return SniffZipContainer(filePath);

        // gzip 压缩的扁平 ODF（.fodt/.fods/.fodp 的 gzip 变体）
        if (read >= 3 && header[0] == 0x1F && header[1] == 0x8B && header[2] == 0x08) return "gzip";

        // 纯文本族：能认出 HTML 标记就返回 html-marker，否则交回 null 由放宽校验判定
        var plain = DecodeHeaderText(header, read);
        if (plain != null && HasHtmlMarker(plain)) return "html-marker";

        if (header[0] == 0xFF)
        {
            // MPEG 音频同步字：用 layer 位区分 mp3 与 aac(ADTS)
            var b1 = header[1];
            if ((b1 & 0xE0) != 0xE0) return null;
            return (b1 & 0x06) == 0 ? "adts" : "mp3";
        }
        return null;
    }

    /// <summary>
    /// 校验真实格式是否与源扩展名一致（同族互认）。严格模式：语义与历史版本完全一致。
    /// </summary>
    /// <param name="formatToken">Sniff 返回的格式标识；null 表示无法识别</param>
    /// <param name="sourceExt">源（旧）扩展名，归一化小写带点</param>
    /// <returns>一致返回 true</returns>
    public static bool MatchesExtension(string? formatToken, string sourceExt) =>
        MatchesExtension(formatToken, sourceExt, strict: true);

    /// <summary>
    /// 校验真实格式是否与源扩展名一致（同族互认）。
    /// strict=false 只对纯文本族放开，且只承认"负证据"：仅当嗅探结果为空（任何已知格式都不匹配）时才算通过；
    /// Office 文档与图片/音视频永远走严格模式，因此既有行为不受影响。
    /// </summary>
    /// <param name="formatToken">Sniff 返回的格式标识；null 表示无法识别</param>
    /// <param name="sourceExt">源（旧）扩展名，归一化小写带点</param>
    /// <param name="strict">true = 严格模式（默认）；false = 仅对纯文本族允许"无签名"</param>
    /// <returns>一致返回 true</returns>
    public static bool MatchesExtension(string? formatToken, string sourceExt, bool strict)
    {
        if (formatToken == null) return !strict && PathRules.IsTextLikeExtension(sourceExt);
        return ExpectedTokens(sourceExt).Contains(formatToken);
    }

    /// <summary>扩展名对应的可接受格式标识集合（同族互认）。</summary>
    /// <param name="ext">归一化扩展名</param>
    private static HashSet<string> ExpectedTokens(string ext) => ext switch
    {
        // —— 图片 ——
        ".jpg" or ".jpeg" => new HashSet<string> { "jpeg" },
        ".png" => new HashSet<string> { "png" },
        ".bmp" => new HashSet<string> { "bmp" },
        ".gif" => new HashSet<string> { "gif" },
        ".webp" => new HashSet<string> { "webp" },
        ".tiff" => new HashSet<string> { "tiff" },
        ".ico" => new HashSet<string> { "ico" },
        // —— 音视频 ——
        ".mp4" or ".mov" or ".m4a" => new HashSet<string> { "isobmff" },
        ".mkv" or ".webm" => new HashSet<string> { "ebml" },
        ".avi" => new HashSet<string> { "riff-avi" },
        ".wav" => new HashSet<string> { "riff-wav" },
        ".wmv" => new HashSet<string> { "asf" },
        ".mp3" => new HashSet<string> { "mp3" },
        ".flac" => new HashSet<string> { "flac" },
        ".aac" => new HashSet<string> { "adts", "adif" },
        ".ogg" => new HashSet<string> { "ogg" },
        ".ts" => new HashSet<string> { "mpegts" },
        // —— 文档：OOXML（ZIP 容器，按条目列表分流）——
        ".docx" or ".docm" or ".dotx" => new HashSet<string> { "ooxml-word" },
        ".pptx" or ".pptm" or ".potx" => new HashSet<string> { "ooxml-presentation" },
        ".xlsx" or ".xlsm" or ".xltx" => new HashSet<string> { "ooxml-spreadsheet" },
        // —— 文档：ODF ——
        ".odt" or ".ott" => new HashSet<string> { "odf-text" },
        ".ods" or ".ots" => new HashSet<string> { "odf-spreadsheet" },
        ".odp" or ".otp" => new HashSet<string> { "odf-presentation" },
        // —— 文档：旧版二进制 / RTF / PDF ——
        ".doc" => new HashSet<string> { "ole-word" },
        ".xls" => new HashSet<string> { "ole-excel" },
        ".ppt" => new HashSet<string> { "ole-powerpoint" },
        ".rtf" => new HashSet<string> { "rtf" },
        ".pdf" => new HashSet<string> { "pdf" },
        // —— 文档：纯文本族（无 magic，靠负证据放行；html 额外要求 HTML 标记）——
        ".csv" or ".tsv" => new HashSet<string> { "plaintext-passthrough" },
        ".md" or ".markdown" => new HashSet<string> { "plaintext-passthrough", "html-marker" },
        ".html" or ".htm" or ".xhtml" => new HashSet<string> { "html-marker" },
        _ => new HashSet<string>()
    };

    /// <summary>ZIP 签名：普通/空/分卷三种本地文件头都算。</summary>
    /// <param name="header">文件头字节</param>
    /// <param name="read">实际读到的字节数</param>
    private static bool IsZipSignature(byte[] header, int read) =>
        read >= 4 && header[0] == 0x50 && header[1] == 0x4B
        && (header[2] == 0x03 || header[2] == 0x05 || header[2] == 0x07);

    /// <summary>
    /// ZIP 容器分流：读取真实条目列表，按 OOXML 主部件路径或 ODF mimetype 判定具体文档类型。
    /// 读不出来（损坏、加密、非 ZIP）时返回 "zip"，交给"magic 不符"拦下。
    /// </summary>
    /// <param name="filePath">文件路径</param>
    private static string SniffZipContainer(string filePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(filePath);
            var names = new List<string>(archive.Entries.Count);
            foreach (var entry in archive.Entries) names.Add(entry.FullName);

            // —— ODF：第一个条目是 stored 的 mimetype ——
            for (var i = 0; i < names.Count; i++)
            {
                if (!string.Equals(names[i], "mimetype", StringComparison.OrdinalIgnoreCase)) continue;
                var mime = ReadEntryText(archive.Entries[i]);
                var odf = SniffOdfMime(mime);
                if (odf != null) return odf;
                if (i > 1) break;
            }

            // —— OOXML：依次找主部件路径 ——
            foreach (var name in names)
            {
                if (name.StartsWith("word/", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase)) return "ooxml-word";
                if (name.StartsWith("ppt/", StringComparison.OrdinalIgnoreCase)) return "ooxml-presentation";
                if (name.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)) return "ooxml-spreadsheet";
            }
            return "zip";
        }
        catch (Exception)
        {
            // 损坏或加密的 ZIP：按普通压缩包处理（不在任何文档扩展名的期望集合内 ⇒ 跳过）
            return "zip";
        }
    }

    /// <summary>读一个 ZIP 条目的文本内容（只用于极小的 mimetype 条目）。</summary>
    /// <param name="entry">ZIP 条目</param>
    private static string ReadEntryText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var buffer = new char[256];
        var count = reader.Read(buffer, 0, buffer.Length);
        return new string(buffer, 0, count);
    }

    /// <summary>ODF mimetype 字符串 → 格式标识（未收录的 ODF 类型返回 null，按普通 zip 处理）。</summary>
    /// <param name="mime">mimetype 条目内容</param>
    private static string? SniffOdfMime(string mime)
    {
        if (mime.Contains("application/vnd.oasis.opendocument.text", StringComparison.Ordinal)) return "odf-text";
        if (mime.Contains("application/vnd.oasis.opendocument.spreadsheet", StringComparison.Ordinal)) return "odf-spreadsheet";
        if (mime.Contains("application/vnd.oasis.opendocument.presentation", StringComparison.Ordinal)) return "odf-presentation";
        return null;
    }

    /// <summary>
    /// OLE2（doc / xls / ppt 共用签名）分流：读偏移 0x30 处的 UTF-16LE 根目录条目名。
    /// 宏文档可能把 _VBA_PROJECT_CUR 放在前面，因此先按该偏移判断，判不出再在整个头窗口里找特征名；
    /// 都找不到时返回 "ole2"（不在任何期望集合内 ⇒ 跳过），失败方向安全。
    /// </summary>
    /// <param name="header">文件头字节</param>
    /// <param name="read">实际读到的字节数</param>
    private static string SniffOle2(byte[] header, int read)
    {
        const int nameOffset = 0x30;
        const int nameLength = 32;
        if (read >= nameOffset + nameLength)
        {
            var name = Encoding.Unicode.GetString(header, nameOffset, nameLength).TrimEnd('\0');
            var token = Ole2NameToToken(name);
            if (token != null) return token;
        }
        var text = DecodeHeaderText(header, read) ?? string.Empty;
        if (text.Contains("WordDocument", StringComparison.Ordinal)) return "ole-word";
        if (text.Contains("Workbook", StringComparison.Ordinal) || text.Contains("Book", StringComparison.Ordinal)) return "ole-excel";
        if (text.Contains("PowerPoint", StringComparison.Ordinal)) return "ole-powerpoint";
        return "ole2";
    }

    /// <summary>OLE2 根目录条目名 → 格式标识。</summary>
    /// <param name="name">偏移 0x30 处的 UTF-16LE 条目名</param>
    private static string? Ole2NameToToken(string name)
    {
        if (name.StartsWith("WordDocument", StringComparison.Ordinal)) return "ole-word";
        if (name.StartsWith("Workbook", StringComparison.Ordinal) || name.Equals("Book", StringComparison.Ordinal)) return "ole-excel";
        if (name.StartsWith("PowerPoint", StringComparison.Ordinal)) return "ole-powerpoint";
        return null;
    }

    /// <summary>
    /// 把文件头按文本解码（UTF-8 / BOM 感知）。出现 NUL 字节时返回 null：
    /// NUL 只会出现在二进制内容里，此时任何"文本标记"判断都不可信。
    /// </summary>
    /// <param name="header">文件头字节</param>
    /// <param name="read">实际读到的字节数</param>
    private static string? DecodeHeaderText(byte[] header, int read)
    {
        for (var i = 0; i < read; i++)
        {
            if (header[i] == 0) return null;
        }
        var start = 0;
        // 跳过 BOM（UTF-8 / UTF-16LE / UTF-16BE），让标记匹配不受编码前缀影响
        if (read >= 3 && header[0] == 0xEF && header[1] == 0xBB && header[2] == 0xBF) start = 3;
        else if (read >= 2 && ((header[0] == 0xFF && header[1] == 0xFE) || (header[0] == 0xFE && header[1] == 0xFF))) start = 2;
        try
        {
            return Encoding.UTF8.GetString(header, start, read - start);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>是否包含 HTML 标记（不区分大小写）：纯文本族里 html 的唯一可校验特征。</summary>
    /// <param name="text">已解码的文件头文本</param>
    private static bool HasHtmlMarker(string text) =>
        text.Contains("<!doctype html", StringComparison.OrdinalIgnoreCase)
        || text.Contains("<html", StringComparison.OrdinalIgnoreCase)
        || text.Contains("<head", StringComparison.OrdinalIgnoreCase)
        || text.Contains("<body", StringComparison.OrdinalIgnoreCase);

    /// <summary>判断文件头是否为 ASF 容器 GUID（wmv/wma）。</summary>
    /// <param name="h">文件头字节</param>
    private static bool IsAsfGuid(byte[] h) =>
        h[0] == 0x30 && h[1] == 0x26 && h[2] == 0xB2 && h[3] == 0x75 && h[4] == 0x8E && h[5] == 0x66
        && h[6] == 0xCF && h[7] == 0x11 && h[8] == 0xA6 && h[9] == 0xD9 && h[10] == 0x00 && h[11] == 0xAA
        && h[12] == 0x00 && h[13] == 0x62 && h[14] == 0xCE && h[15] == 0x6C;
}
