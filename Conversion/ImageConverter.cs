using ImageMagick;
using RenamePro.Core;

namespace RenamePro.Conversion;

/// <summary>
/// 图片转换器：基于 Magick.NET（唯一允许的第三方包）完成位图格式转换。
/// 资源控制：Magick 程序集**延迟加载**（只有本类引用 Magick 类型，首次图片转换才会加载）；
/// 内存上限（约 512MB）在首次转换时惰性设置——启动时设置会破坏延迟加载。
/// 策略：同大类图片尽量互转；gif 源按 config.gifPolicy 取首帧（skip 由主流程提前忽略）；
/// 仅 jpg 输出有损（质量取 config.imageQuality），其余输出无损；进度经 Magick 进度事件回调百分比。
/// </summary>
public static class ImageConverter
{
    /// <summary>Magick 内存上限：512MB。</summary>
    private const ulong MemoryLimitBytes = 512UL * 1024 * 1024;

    /// <summary>是否已完成 Magick 惰性初始化。</summary>
    private static bool _magickInitialized;

    /// <summary>
    /// 首次图片转换时惰性初始化 Magick：设置内存上限，防止大图转换拖垮系统。
    /// </summary>
    private static void EnsureMagickInitialized()
    {
        if (_magickInitialized) return;
        _magickInitialized = true;
        try
        {
            ResourceLimits.Memory = MemoryLimitBytes;
            Log.Info($"Magick 已加载（内存上限 {MemoryLimitBytes / (1024 * 1024)}MB）");
        }
        catch (Exception ex)
        {
            Log.Warn($"设置 Magick 内存上限失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 执行图片转换：读取 sourcePath（内容为旧格式）写出 targetPath（新格式临时文件）。
    /// </summary>
    /// <param name="task">任务模型（提供扩展名与进度上报）</param>
    /// <param name="sourcePath">源文件（改名后的文件，内容仍是旧格式）</param>
    /// <param name="targetPath">目标临时文件（Guid 命名 + 新扩展名）</param>
    /// <param name="token">取消令牌</param>
    public static void Convert(ConversionTask task, string sourcePath, string targetPath, CancellationToken token)
    {
        EnsureMagickInitialized();

        // gifPolicy=first-frame：gif 源只取首帧（skip 策略已在主流程提前忽略）
        var settings = new MagickReadSettings();
        if (task.OldExt == ".gif")
        {
            settings.FrameIndex = 0;
            settings.FrameCount = 1;
        }

        using var image = new MagickImage(sourcePath, settings);
        token.ThrowIfCancellationRequested();

        // Magick 进度事件回调百分比（ProgressEventArgs.Progress 即 0~100）；取消请求经 Cancel 生效
        if (task.Progress != null)
        {
            image.Progress += (_, e) =>
            {
                if (token.IsCancellationRequested) e.Cancel = true;
                // Progress 为 Percentage 类型（0% = 0.0，100% = 100.0），显式转 double 后钳到 0~100
                task.Progress.Report((int)Math.Clamp((double)e.Progress, 0, 100));
            };
        }

        if (task.NewExt is ".jpg" or ".jpeg")
        {
            // 仅 jpg 输出有损，质量来自 config.json（MagickImage.Quality 为 uint）
            image.Quality = (uint)AppConfig.Current.ImageQuality;
            if (image.HasAlpha)
            {
                // 透明铺白底，避免去除 alpha 后透明区域变黑
                image.BackgroundColor = MagickColors.White;
                image.Alpha(AlphaOption.Remove);
            }
        }
        else if (task.NewExt == ".webp")
        {
            // webp 输出走无损编码（其余格式默认即无损）
            image.Settings.SetDefine(MagickFormat.WebP, "lossless", true);
        }

        token.ThrowIfCancellationRequested();
        image.Write(targetPath);
        task.Progress?.Report(100);
    }
}