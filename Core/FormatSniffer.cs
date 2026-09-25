using System.Text;

namespace RenamePro.Core;

/// <summary>
/// 文件格式嗅探器：读取文件头 magic number 识别真实格式，
/// 并校验真实格式是否与源（改名前的）扩展名一致——统一前置校验，不一致的任务直接跳过。
/// 同族格式互认（mp4/mov/m4a 同为 isobmff，mkv/webm 同为 ebml）。
/// </summary>
public static class FormatSniffer
{
    /// <summary>
    /// 嗅探文件头并返回归一化格式标识（如 jpeg / png / isobmff）；无法识别返回 null。
    /// </summary>
    /// <param name="filePath">文件完整路径（直接使用 path 重载，兼容中文与空格）</param>
    /// <returns>格式标识或 null</returns>
    public static string? Sniff(string filePath)
    {
        var header = new byte[32];
        int read;
        using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            read = stream.Read(header, 0, header.Length);
        }
        if (read < 4) return null; // 文件太小，认不出

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
    /// 校验真实格式是否与源扩展名一致（同族互认）。
    /// </summary>
    /// <param name="formatToken">Sniff 返回的格式标识；null 表示无法识别</param>
    /// <param name="sourceExt">源（旧）扩展名，归一化小写带点</param>
    /// <returns>一致返回 true</returns>
    public static bool MatchesExtension(string? formatToken, string sourceExt)
    {
        if (formatToken == null) return false;
        return ExpectedTokens(sourceExt).Contains(formatToken);
    }

    /// <summary>扩展名对应的可接受格式标识集合（同族互认）。</summary>
    /// <param name="ext">归一化扩展名</param>
    private static HashSet<string> ExpectedTokens(string ext) => ext switch
    {
        ".jpg" or ".jpeg" => new HashSet<string> { "jpeg" },
        ".png" => new HashSet<string> { "png" },
        ".bmp" => new HashSet<string> { "bmp" },
        ".gif" => new HashSet<string> { "gif" },
        ".webp" => new HashSet<string> { "webp" },
        ".tiff" => new HashSet<string> { "tiff" },
        ".ico" => new HashSet<string> { "ico" },
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
        _ => new HashSet<string>()
    };

    /// <summary>判断文件头是否为 ASF 容器 GUID（wmv/wma）。</summary>
    /// <param name="h">文件头字节</param>
    private static bool IsAsfGuid(byte[] h) =>
        h[0] == 0x30 && h[1] == 0x26 && h[2] == 0xB2 && h[3] == 0x75 && h[4] == 0x8E && h[5] == 0x66
        && h[6] == 0xCF && h[7] == 0x11 && h[8] == 0xA6 && h[9] == 0xD9 && h[10] == 0x00 && h[11] == 0xAA
        && h[12] == 0x00 && h[13] == 0x62 && h[14] == 0xCE && h[15] == 0x6C;
}