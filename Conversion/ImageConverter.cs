using ImageMagick;

namespace RenamePro.Conversion;

/// <summary>
/// 图片转换器：基于 Magick.NET（唯一允许的第三方包）完成位图格式转换。
/// 策略：同大类图片尽量互转；gif 源取首帧（gifPolicy=first-frame）；
/// 仅 jpg 输出有损（质量 92），其余输出无损；进度经 Magick 进度事件回调百分比。
/// </summary>
public static class ImageConverter
{
    /// <summary>JPEG 输出质量（规格默认 92；里程碑 4 起由 config.json 的 imageQuality 提供）。</summary>
    private const int JpegQuality = 92;

    /// <summary>
    /// 执行图片转换：读取 sourcePath（内容为旧格式）写出 targetPath（新格式临时文件）。
    /// </summary>
    /// <param name="task">任务模型（提供扩展名与进度上报）</param>
    /// <param name="sourcePath">源文件（改名后的文件，内容仍是旧格式）</param>
    /// <param name="targetPath">目标临时文件（Guid 命名 + 新扩展名）</param>
    /// <param name="token">取消令牌</param>
    public static void Convert(ConversionTask task, string sourcePath, string targetPath, CancellationToken token)
    {
        // gifPolicy=first-frame：gif 源只取首帧（里程碑 4 起可配置为 skip）
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
            // 仅 jpg 输出有损
            image.Quality = JpegQuality;
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