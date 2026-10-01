namespace RenamePro.Core;

/// <summary>
/// 路径判定规则：扩展名白名单、排除目录、排除文件模式。
/// 判定严格按开销升序早退：扩展名白名单判定 → 排除目录前缀 → 排除文件模式。
/// 全部路径比较均使用 OrdinalIgnoreCase / ToLowerInvariant，兼容中文与空格路径（不做任何 URI 转换）。
/// </summary>
public static class PathRules
{
    /// <summary>图片扩展名集合（归一化：小写、带前导点）。</summary>
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.Ordinal)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tiff", ".ico"
    };

    /// <summary>音视频扩展名集合（归一化：小写、带前导点）。</summary>
    private static readonly HashSet<string> AudioVideoExtensions = new(StringComparer.Ordinal)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".mp3", ".wav", ".flac", ".aac", ".ogg",
        // v2.0 放宽：补全规格容器矩阵所需的扩展名，使 ts/webm/flv/m4a 的改名可进入转换流程
        ".ts", ".webm", ".flv", ".m4a"
    };

    /// <summary>
    /// 文档扩展名集合（归一化：小写、带前导点）。
    /// 刻意不含 .txt：把 txt 改名成别的东西是高频误操作，而纯文本没有 magic 可校验，
    /// 收录它只会让"无法确认内容"的文档进入外置引擎（与"宁可漏转换"冲突）。
    /// 因此只收录 Office / ODF / Markdown / HTML / CSV 这类有明确结构或用途的格式。
    /// </summary>
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.Ordinal)
    {
        // Word / 文字处理
        ".docx", ".docm", ".dotx", ".doc", ".odt", ".ott", ".rtf", ".md", ".markdown", ".html", ".htm", ".xhtml",
        // PowerPoint / 演示文稿
        ".pptx", ".pptm", ".potx", ".ppt", ".odp", ".otp",
        // Excel / 电子表格
        ".xlsx", ".xlsm", ".xltx", ".xls", ".ods", ".ots", ".csv", ".tsv",
        // PDF：仅当 allowPdfSource=true 时才作为源可用，作为目标始终可用
        ".pdf"
    };

    /// <summary>
    /// 纯文本族扩展名：内容没有 magic number，格式校验只能走"负证据"路径
    /// （见 <see cref="FormatSniffer.MatchesExtension(string?, string, bool)"/>）。
    /// </summary>
    private static readonly HashSet<string> TextLikeExtensions = new(StringComparer.Ordinal)
    {
        ".md", ".markdown", ".csv", ".tsv", ".html", ".htm", ".xhtml"
    };

    /// <summary>任意盘符下都排除的目录名（按路径段匹配）。</summary>
    private static readonly string[] ExcludedDirectoryNames = { "$Recycle.Bin", "System Volume Information" };

    /// <summary>
    /// 判定一次改名是否需要处理。
    /// </summary>
    /// <param name="oldPath">改名前的完整路径</param>
    /// <param name="newPath">改名后的完整路径</param>
    /// <returns>是否需要处理 + 判定原因（忽略时用于日志验证）</returns>
    public static (bool ShouldProcess, string Reason) Judge(string oldPath, string newPath)
    {
        // —— 第一优先级：扩展名白名单判定（开销最小，先做）——
        var oldExt = NormalizeExt(Path.GetExtension(oldPath));
        var newExt = NormalizeExt(Path.GetExtension(newPath));
        // 仅大小写变化（a.JPG → a.jpg）归一化后相同，视为扩展名未变化，静默忽略
        if (oldExt == newExt) return (false, "扩展名未变化（含仅大小写差异）");
        bool sameImageSet = ImageExtensions.Contains(oldExt) && ImageExtensions.Contains(newExt);
        bool sameAvSet = AudioVideoExtensions.Contains(oldExt) && AudioVideoExtensions.Contains(newExt);
        // 文档集合放宽为"任一端属于文档"：跨族组合（docx → xlsx、pdf → docx）需要走到转换主流程，
        // 才能给出"只允许目标为 pdf""需要 allowPdfSource"这类可操作的具体原因；
        // 在这里一刀切忽略只会留下一条含义模糊的日志。
        bool anyDoc = DocumentExtensions.Contains(oldExt) || DocumentExtensions.Contains(newExt);
        if (!sameImageSet && !sameAvSet && !anyDoc) return (false, "新旧扩展名不属于同一媒体集合");

        // —— 第二优先级：排除目录前缀（新旧路径任一命中即忽略）——
        if (IsUnderExcludedDirectory(oldPath)) return (false, "旧路径位于排除目录");
        if (IsUnderExcludedDirectory(newPath)) return (false, "新路径位于排除目录");

        // —— 第三优先级：排除文件模式（~$ 开头、点开头）——
        if (IsExcludedFileName(oldPath)) return (false, "旧文件名命中排除模式（~$ 或 . 开头）");
        if (IsExcludedFileName(newPath)) return (false, "新文件名命中排除模式（~$ 或 . 开头）");

        return (true, "通过全部判定");
    }

    /// <summary>扩展名归一化：统一小写（Path.GetExtension 返回值已带前导点，空扩展名返回空串）。</summary>
    /// <param name="extension">原始扩展名</param>
    /// <returns>归一化后的扩展名</returns>
    public static string NormalizeExt(string? extension) =>
        string.IsNullOrEmpty(extension) ? string.Empty : extension.ToLowerInvariant();

    /// <summary>判断扩展名（归一化后）是否属于图片集合。</summary>
    /// <param name="ext">归一化扩展名</param>
    public static bool IsImageExtension(string ext) => ImageExtensions.Contains(ext);

    /// <summary>判断扩展名（归一化后）是否属于音视频集合。</summary>
    /// <param name="ext">归一化扩展名</param>
    public static bool IsAudioVideoExtension(string ext) => AudioVideoExtensions.Contains(ext);

    /// <summary>判断扩展名（归一化后）是否属于文档集合。</summary>
    /// <param name="ext">归一化扩展名</param>
    public static bool IsDocumentExtension(string ext) => DocumentExtensions.Contains(ext);

    /// <summary>
    /// 判断扩展名是否"在文档管线的可读范围内"：即是否可能作为一次文档转换的**源**。
    /// 用于把"这个格式根本不进管线"和"内容与后缀不符"区分开，给出更准确的跳过原因。
    /// </summary>
    /// <param name="ext">归一化扩展名</param>
    public static bool IsDocumentSourceCandidate(string ext) =>
        DocumentExtensions.Contains(ext) || TextLikeExtensions.Contains(ext);

    /// <summary>
    /// 判断扩展名（归一化后）是否属于纯文本族（内容无 magic，需走放宽的格式校验）。
    /// </summary>
    /// <param name="ext">归一化扩展名</param>
    public static bool IsTextLikeExtension(string ext) => TextLikeExtensions.Contains(ext);

    /// <summary>
    /// 判断路径是否位于排除目录内：
    /// 系统目录（Windows、Program Files、Program Files (x86)）做前缀匹配；
    /// $Recycle.Bin、System Volume Information 做路径段匹配（覆盖所有盘符）。
    /// </summary>
    /// <param name="path">待判断的完整路径</param>
    /// <returns>命中排除规则返回 true</returns>
    private static bool IsUnderExcludedDirectory(string path)
    {
        string full;
        try
        {
            // 归一化为绝对路径再比较，去掉末尾分隔符便于前缀判断
            full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            // 路径非法按“排除”处理：宁可漏转换，也不错转换
            return true;
        }

        // 系统目录用 API 取实际位置（兼容系统装在非 C 盘的情况）
        if (IsUnderPrefix(full, Environment.GetFolderPath(Environment.SpecialFolder.Windows))
            || IsUnderPrefix(full, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
            || IsUnderPrefix(full, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)))
            return true;

        // 路径段匹配：任意盘符下的回收站 / 卷影副本目录
        foreach (var segment in full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            foreach (var name in ExcludedDirectoryNames)
            {
                if (string.Equals(segment, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 判断 full 是否位于 prefix 目录之内（含 prefix 本身）。
    /// 必须带分隔符判断，避免 C:\WindowsOld 误判为 C:\Windows 之内。
    /// </summary>
    /// <param name="full">归一化后的完整路径</param>
    /// <param name="prefix">排除目录前缀</param>
    private static bool IsUnderPrefix(string full, string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return false;
        var trimmed = prefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.Equals(trimmed, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(trimmed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>排除文件模式：~$ 开头（Office 临时文件）或点开头（隐藏/系统文件）。</summary>
    /// <param name="path">完整路径</param>
    private static bool IsExcludedFileName(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("~$", StringComparison.Ordinal) || name.StartsWith(".", StringComparison.Ordinal);
    }
}