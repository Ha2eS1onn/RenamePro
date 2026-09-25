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
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".mp3", ".wav", ".flac", ".aac", ".ogg"
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
        if (!sameImageSet && !sameAvSet) return (false, "新旧扩展名不属于同一媒体集合");

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