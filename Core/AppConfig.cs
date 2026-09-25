using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RenamePro.Core;

/// <summary>GIF 源处理策略。</summary>
public enum GifPolicyMode
{
    /// <summary>取首帧转换（默认）。</summary>
    FirstFrame,

    /// <summary>遇到 gif 改名直接忽略。</summary>
    Skip
}

/// <summary>配置数据（与程序同目录 config.json 一一对应）。</summary>
public sealed class AppConfigData
{
    /// <summary>转换前是否创建原格式副本（默认 true）。</summary>
    public bool EnableBackup { get; set; } = true;

    /// <summary>转换失败时是否自动回滚（删目标文件 + 副本移回原名，默认 false 保守策略）。</summary>
    public bool AutoRollbackOnFailure { get; set; }

    /// <summary>全部任务完成后是否弹 Windows Toast 汇总（默认 true）。</summary>
    public bool EnableToast { get; set; } = true;

    /// <summary>是否显示系统进度对话框；false = 静默转换，完全不初始化 Shell COM（默认 true）。</summary>
    public bool ShowProgressDialog { get; set; } = true;

    /// <summary>JPEG 输出质量 1~100（仅对转出 jpg 生效，默认 92）。</summary>
    public int ImageQuality { get; set; } = 92;

    /// <summary>GIF 策略："first-frame"（默认）或 "skip"。</summary>
    public string GifPolicy { get; set; } = "first-frame";

    /// <summary>图片队列并发度 1~8（默认 2）。</summary>
    public int ImageConcurrency { get; set; } = 2;

    /// <summary>监听的盘符列表（如 ["D:\\"]）；空列表 = 全部固定磁盘。</summary>
    public List<string> WatchDrives { get; set; } = new();

    /// <summary>是否跳过云盘按需占位文件（RecallOnDataAccess，默认 true）。</summary>
    public bool SkipCloudFiles { get; set; } = true;

    /// <summary>GIF 策略枚举化（非 "skip" 一律按首帧处理）。</summary>
    [JsonIgnore]
    public GifPolicyMode GifMode =>
        string.Equals(GifPolicy?.Trim(), "skip", StringComparison.OrdinalIgnoreCase)
            ? GifPolicyMode.Skip
            : GifPolicyMode.FirstFrame;
}

/// <summary>
/// 程序配置（程序同目录 config.json）：
/// 首次运行生成带中文注释的默认配置；读取宽容（允许注释与尾逗号），
/// 任何解析错误都回退默认值并记日志，绝不影响程序运行；支持托盘菜单“重新加载配置”。
/// </summary>
public static class AppConfig
{
    /// <summary>默认配置文件内容（带中文注释；解析时允许注释）。</summary>
    private const string DefaultFileContent = """
{
  // ================= RenamePro 配置 =================
  // 修改本文件后，可点托盘菜单“重新加载配置”即时生效（个别项需重启）。

  // 转换前是否创建原格式副本（强烈建议保持 true）
  "enableBackup": true,

  // 转换失败时是否自动回滚：删除目标文件，并把副本移回改名前的原名（默认 false，保守策略）
  "autoRollbackOnFailure": false,

  // 全部任务完成后是否弹 Windows Toast 汇总通知
  "enableToast": true,

  // 是否显示系统进度对话框（false = 静默转换，完全不初始化 Shell COM）
  "showProgressDialog": true,

  // JPEG 输出质量（1~100，仅对转出 jpg 生效）
  "imageQuality": 92,

  // GIF 改名策略："first-frame" = 取首帧转换；"skip" = 直接忽略
  "gifPolicy": "first-frame",

  // 图片队列并发度（1~8）
  "imageConcurrency": 2,

  // 监听的盘符列表，例如 ["D:\\", "E:\\"]；留空 [] = 监听所有固定磁盘
  "watchDrives": [],

  // 是否跳过云盘按需占位文件（RecallOnDataAccess）
  "skipCloudFiles": true
}
""";

    /// <summary>解析选项：允许注释与尾逗号，属性名大小写不敏感。</summary>
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>配置访问锁。</summary>
    private static readonly object Sync = new();

    /// <summary>当前配置快照。</summary>
    private static AppConfigData _current = new();

    /// <summary>配置文件完整路径（程序同目录）。</summary>
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "config.json");

    /// <summary>当前配置快照（各模块按需读取，天然支持重新加载）。</summary>
    public static AppConfigData Current
    {
        get { lock (Sync) return _current; }
    }

    /// <summary>启动时确保配置已加载；首次运行自动生成带注释的默认配置文件。</summary>
    public static void EnsureLoaded()
    {
        lock (Sync)
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    // UTF-8 带 BOM：记事本打开中文注释不乱码
                    File.WriteAllText(FilePath, DefaultFileContent, new UTF8Encoding(true));
                    Log.Info($"已生成默认配置文件：{FilePath}");
                }
                _current = Parse(File.ReadAllText(FilePath, Encoding.UTF8));
                Log.Info($"配置已加载：备份={_current.EnableBackup}，失败回滚={_current.AutoRollbackOnFailure}，Toast={_current.EnableToast}，进度框={_current.ShowProgressDialog}，图片质量={_current.ImageQuality}，gif={_current.GifPolicy}，图片并发={_current.ImageConcurrency}，监听盘={(_current.WatchDrives.Count == 0 ? "全部固定磁盘" : string.Join(",", _current.WatchDrives))}，跳过云盘占位={_current.SkipCloudFiles}");
            }
            catch (Exception ex)
            {
                Log.Warn($"读取配置失败，使用默认配置：{ex.Message}");
                _current = new AppConfigData();
            }
        }
    }

    /// <summary>重新加载配置（托盘菜单调用）。</summary>
    /// <returns>成功返回 true；解析失败返回 false（保留旧配置）</returns>
    public static bool Reload()
    {
        lock (Sync)
        {
            try
            {
                _current = Parse(File.ReadAllText(FilePath, Encoding.UTF8));
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"重新加载配置失败，继续使用当前配置：{ex.Message}");
                return false;
            }
        }
    }

    /// <summary>解析并校验配置文本（越界值钳制 + 冲突提示）。</summary>
    /// <param name="json">config.json 文本</param>
    private static AppConfigData Parse(string json)
    {
        var data = JsonSerializer.Deserialize<AppConfigData>(json, ReadOptions) ?? new AppConfigData();

        if (data.ImageQuality is < 1 or > 100)
        {
            Log.Warn($"配置 imageQuality={data.ImageQuality} 越界，已钳制到 1~100");
            data.ImageQuality = Math.Clamp(data.ImageQuality, 1, 100);
        }
        if (data.ImageConcurrency is < 1 or > 8)
        {
            Log.Warn($"配置 imageConcurrency={data.ImageConcurrency} 越界，已钳制到 1~8");
            data.ImageConcurrency = Math.Clamp(data.ImageConcurrency, 1, 8);
        }
        data.WatchDrives ??= new List<string>();
        data.WatchDrives.RemoveAll(string.IsNullOrWhiteSpace);
        if (data.AutoRollbackOnFailure && !data.EnableBackup)
        {
            Log.Warn("配置冲突：autoRollbackOnFailure=true 但 enableBackup=false，失败时没有副本可回滚");
        }
        return data;
    }
}