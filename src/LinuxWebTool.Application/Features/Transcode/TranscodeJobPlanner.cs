
namespace LinuxWebTool.Application.Features.Transcode;

/// <summary>
/// 转码作业路径与模式规划器（纯领域逻辑）：
/// - 并存模式（Alongside）路径推导与后缀规整；
/// - 替换模式（Replace）临时中间文件（.lwt-tmp）生成策略；
/// - 预设与自定义参数互斥与继承判定。
/// </summary>
public static class TranscodeJobPlanner
{
    public const string TempFileSuffix = ".lwt-tmp";

    public static string PlanOutputPath(string sourcePath, string? outputDir, string containerExtension, TranscodeOutputMode mode)
    {
        var ext = containerExtension.StartsWith('.') ? containerExtension : $".{containerExtension}";
        var dir = string.IsNullOrWhiteSpace(outputDir)
            ? Path.GetDirectoryName(sourcePath) ?? string.Empty
            : outputDir.Trim();

        var baseName = Path.GetFileNameWithoutExtension(sourcePath);

        if (mode == TranscodeOutputMode.Replace)
        {
            // 替换模式：生成在同级目录下的安全临时隐藏文件
            return Path.Combine(dir, $"{baseName}{TempFileSuffix}{ext}");
        }

        // 并存模式：若源文件与目标文件同扩展名，自动追加 _transcoded 防止覆盖原文件
        var sourceExt = Path.GetExtension(sourcePath);
        var targetFileName = string.Equals(sourceExt, ext, StringComparison.OrdinalIgnoreCase)
            ? $"{baseName}_transcoded{ext}"
            : $"{baseName}{ext}";

        return Path.Combine(dir, targetFileName);
    }

    public static (bool IsValid, string? ErrorMessage) ValidateSubmission(TranscodeSubmitRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourcePath))
        {
            return (false, "请填写源文件 / 源文件夹路径");
        }

        if (string.IsNullOrWhiteSpace(request.CustomArgs) && request.PresetId is null)
        {
            return (false, "请选择预设或填写自定义 ffmpeg 参数");
        }

        return (true, null);
    }
}
