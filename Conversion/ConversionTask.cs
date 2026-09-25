using RenamePro.Core;

namespace RenamePro.Conversion;

/// <summary>转换任务的处理结果（对应日志中的 OK / FAILED / SKIPPED / CANCELLED）。</summary>
public enum ConversionResult
{
    /// <summary>转换并原子替换成功。</summary>
    Ok,

    /// <summary>任务失败（转换或替换出错），默认不回滚。</summary>
    Failed,

    /// <summary>按规则跳过（magic 不符、特殊文件、不支持组合等）。</summary>
    Skipped,

    /// <summary>被取消（副本保留）。</summary>
    Cancelled
}

/// <summary>
/// 一次“扩展名改名触发的格式转换”任务的描述模型：携带旧/新路径、归一化扩展名与进度上报入口。
/// </summary>
public sealed class ConversionTask
{
    /// <summary>
    /// 创建任务模型。
    /// </summary>
    /// <param name="oldPath">改名前完整路径（内容真实格式 = 旧扩展名）</param>
    /// <param name="newPath">改名后完整路径（待转换文件当前所在）</param>
    public ConversionTask(string oldPath, string newPath)
    {
        OldPath = oldPath;
        NewPath = newPath;
        OldExt = PathRules.NormalizeExt(Path.GetExtension(oldPath));
        NewExt = PathRules.NormalizeExt(Path.GetExtension(newPath));
        StartTime = DateTime.UtcNow;
    }

    /// <summary>改名前的完整路径。</summary>
    public string OldPath { get; }

    /// <summary>改名后的完整路径。</summary>
    public string NewPath { get; }

    /// <summary>源（旧）扩展名，归一化小写带点。</summary>
    public string OldExt { get; }

    /// <summary>目标（新）扩展名，归一化小写带点。</summary>
    public string NewExt { get; }

    /// <summary>任务开始时间（用于耗时统计）。</summary>
    public DateTime StartTime { get; }

    /// <summary>进度上报（百分比 0~100）；里程碑 2 接日志，里程碑 3 接进度对话框。</summary>
    public IProgress<int>? Progress { get; set; }

    /// <summary>任务处理结果；null 表示尚未产生结论（进度聚合据此统计成功/失败/跳过/取消）。</summary>
    public ConversionResult? Result { get; set; }

    /// <summary>状态文案：正在转换 a.jpg → a.png。</summary>
    public string Description =>
        $"正在转换 {Path.GetFileName(OldPath)} → {Path.GetFileName(NewPath)}";

    /// <summary>已耗时（秒）。</summary>
    public double ElapsedSeconds => (DateTime.UtcNow - StartTime).TotalSeconds;
}