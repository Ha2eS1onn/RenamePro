using System.Globalization;
using RenamePro.Core;

namespace RenamePro.Conversion;

/// <summary>
/// 备份管理器：转换前把改名后的文件原样字节复制为“原格式副本”（此刻内容仍是旧格式，复制即备份）。
/// 副本命名对齐 Windows 风格并随系统 UI 语言本地化；冲突自动递增；程序永不自动删除副本。
/// </summary>
public static class BackupManager
{
    /// <summary>
    /// 创建备份副本。
    /// </summary>
    /// <param name="sourcePath">改名后的新文件（内容仍为旧格式）</param>
    /// <param name="oldExt">源（旧）扩展名，副本使用旧扩展名以名实一致</param>
    /// <returns>副本的完整路径</returns>
    public static string CreateBackup(string sourcePath, string oldExt)
    {
        var directory = Path.GetDirectoryName(sourcePath)!;
        // 基名取改名后的文件名（对齐 Windows“创建副本”习惯），扩展名取旧格式
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        // 随系统 UI 语言本地化：中文系统“副本”，英文系统“copy”
        var word = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? "副本" : "copy";

        // 冲突自动递增：a - 副本.jpg → a - 副本 (2).jpg → a - 副本 (3).jpg …
        var candidate = Path.Combine(directory, $"{baseName} - {word}{oldExt}");
        for (var n = 2; File.Exists(candidate) || Directory.Exists(candidate); n++)
        {
            candidate = Path.Combine(directory, $"{baseName} - {word} ({n}){oldExt}");
        }

        // 原样字节复制（遇占用按 500ms × 3 重试）
        IoRetry.Run($"创建副本 {Path.GetFileName(candidate)}", () => File.Copy(sourcePath, candidate));
        return candidate;
    }
}