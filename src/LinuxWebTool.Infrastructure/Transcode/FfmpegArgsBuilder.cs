using System.Text;
using LinuxWebTool.Infrastructure.Persistence.Entities;
using LinuxWebTool.Infrastructure.Shell;

namespace LinuxWebTool.Infrastructure.Transcode;

/// <summary>
/// ffmpeg 参数构造：预设（声明式）或自定义参数（{input}/{output} 之外的中间段）二选一。
/// 统一骨架：ffmpeg -hide_banner -y -i &lt;input&gt; [参数段] -progress pipe:1 -nostats &lt;output&gt;
/// 进度经 stdout 的 key=value 行解析，日志走 stderr。
/// </summary>
public static class FfmpegArgsBuilder
{
    // 硬件编码器后端后缀集合：用于从编码器名判断硬件加速类型。
    private static readonly string[] HwBackends = ["nvenc", "vaapi", "qsv", "v4l2m2m", "videotoolbox", "amf", "mfx"];

    /// <summary>
    /// 硬件加速上下文：UseHardwareAccel=用户是否请求，AvailableHwEncoders=当前环境探测到的可用硬件编码器。
    /// </summary>
    public sealed record HwEncodeContext(bool UseHardwareAccel, IReadOnlyList<string> AvailableHwEncoders);

    /// <summary>构造结果：args=完整命令行参数（不含 ffmpeg 可执行文件名），UsedHardwareAccel=实际是否用了硬件编码器。</summary>
    public sealed record BuildResult(List<string> Args, bool UsedHardwareAccel);

    public static List<string> Build(string input, string output, TranscodePreset? preset, string? customArgs)
        => BuildWithHw(input, output, preset, customArgs, new HwEncodeContext(false, [])).Args;

    public static BuildResult BuildWithHw(string input, string output, TranscodePreset? preset, string? customArgs,
        HwEncodeContext? hwContext)
    {
        var hw = hwContext ?? new HwEncodeContext(false, []);
        var args = new List<string> { "-hide_banner", "-y", "-i", input };
        var usedHardware = false;

        if (!string.IsNullOrWhiteSpace(customArgs))
        {
            // 自定义模式：用户完全控制的参数段，系统不干预（不自动附加硬件参数）。
            foreach (var token in ShellArgumentParser.Split(customArgs))
            {
                args.Add(ResolveToken(token, input, output));
            }
        }
        else if (preset is not null)
        {
            var buildResult = BuildPresetTokens(preset, hw);
            args.AddRange(buildResult.Args);
            usedHardware = buildResult.UsedHardwareAccel;
        }

        args.Add("-progress");
        args.Add("pipe:1");
        args.Add("-nostats");
        args.Add(output);
        return new BuildResult(args, usedHardware);
    }

    /// <summary>
    /// 预设参数构造：若请求硬件加速且预设 VideoCodec 是当前环境可用的硬件编码器，则用硬编并附加后端参数；
    /// 否则回退软件编码（UsedHardwareAccel=false）。
    /// </summary>
    private static (List<string> Args, bool UsedHardwareAccel) BuildPresetTokens(TranscodePreset preset, HwEncodeContext hw)
    {
        var tokens = new List<string>();
        var video = (preset.VideoCodec ?? string.Empty).Trim();
        var audio = (preset.AudioCodec ?? string.Empty).Trim();

        var usedHardware = false;
        var hardwareBackend = DetectHardwareBackend(video);
        if (hw.UseHardwareAccel && hardwareBackend is not null && hw.AvailableHwEncoders.Contains(video, StringComparer.OrdinalIgnoreCase))
        {
            // 硬编：前置设备/解码上下文参数，编码器用 -c:v <hardware-codec>，CRF 用 -qp 替代
            tokens.AddRange(BuildHwContextArgs(hardwareBackend));
            tokens.AddRange(["-c:v", video]);
            if (preset.VideoQuality is >= 0 and <= 51)
            {
                tokens.AddRange(["-qp", preset.VideoQuality.Value.ToString()]);
            }
            usedHardware = true;
        }
        else if (video.Length == 0)
        {
            tokens.Add("-vn"); // 纯音频输出
        }
        else if (video.Equals("copy", StringComparison.OrdinalIgnoreCase))
        {
            tokens.AddRange(["-c:v", "copy"]);
        }
        else
        {
            tokens.AddRange(["-c:v", video]);
            if (preset.VideoQuality is >= 0 and <= 51)
            {
                tokens.AddRange(["-crf", preset.VideoQuality.Value.ToString()]);
            }
        }

        if (audio.Length == 0)
        {
            tokens.Add("-an");
        }
        else if (audio.Equals("copy", StringComparison.OrdinalIgnoreCase))
        {
            tokens.AddRange(["-c:a", "copy"]);
        }
        else
        {
            tokens.AddRange(["-c:a", audio]);
            if (!string.IsNullOrWhiteSpace(preset.AudioBitrate))
            {
                tokens.AddRange(["-b:a", preset.AudioBitrate.Trim()]);
            }
        }

        var isCopy = video.Equals("copy", StringComparison.OrdinalIgnoreCase);
        if (preset.Container.Trim().Equals("mp4", StringComparison.OrdinalIgnoreCase) && !isCopy)
        {
            tokens.AddRange(["-movflags", "+faststart"]);
        }

        if (!string.IsNullOrWhiteSpace(preset.ExtraArgs))
        {
            tokens.AddRange(ShellArgumentParser.Split(preset.ExtraArgs));
        }
        return (tokens, usedHardware);
    }

    /// <summary>从编码器名识别硬件后端（如 h264_vaapi → vaapi）；非硬件编码器返回 null。</summary>
    private static string? DetectHardwareBackend(string videoCodec)
    {
        if (string.IsNullOrWhiteSpace(videoCodec))
        {
            return null;
        }
        foreach (var backend in HwBackends)
        {
            if (videoCodec.EndsWith(backend, StringComparison.OrdinalIgnoreCase))
            {
                return backend;
            }
        }
        return null;
    }

    /// <summary>按硬件后端附加解码/设备/像素格式上下文参数（保证硬编不是"名字在但跑不了"）。</summary>
    private static IEnumerable<string> BuildHwContextArgs(string backend) => backend.ToLowerInvariant() switch
    {
        "vaapi" => ["-vaapi_device", "/dev/dri/renderD128", "-hwaccel", "vaapi", "-vf", "format=nv12,hwupload"],
        "nvenc" => ["-hwaccel", "cuda", "-hwaccel_output_format", "cuda"],
        "qsv" => ["-hwaccel", "qsv", "-vf", "format=nv12"],
        "v4l2m2m" => ["-hwaccel", "v4l2m2m"],
        _ => [],
    };

    private static string ResolveToken(string token, string input, string output) => token switch
    {
        "{input}" => input,
        "{output}" => output,
        _ => token,
    };
}

/// <summary>输出路径规划：替换模式走 .lwt-tmp 临时文件（成功后原子替换），并存模式自动避让重名。</summary>
public static class OutputPathPlanner
{
    public const string TempMarker = ".lwt-tmp.";

    public static (string FinalPath, string? TempPath) Plan(string source, string container, TranscodeOutputMode mode, string? outputDir)
    {
        var extension = container.Trim().TrimStart('.');
        if (extension.Length == 0)
        {
            extension = "mp4";
        }

        var directory = string.IsNullOrWhiteSpace(outputDir) ? Path.GetDirectoryName(source) : outputDir.Trim();
        if (string.IsNullOrEmpty(directory))
        {
            directory = ".";
        }
        var baseName = Path.GetFileNameWithoutExtension(source);

        if (mode == TranscodeOutputMode.Replace)
        {
            var final = Path.Combine(directory, $"{baseName}.{extension}");
            var temp = Path.Combine(directory, $"{baseName}{TempMarker}{extension}");
            return (final, temp);
        }

        // 并存：重名自动加序号（源文件自身占用也会避让，避免同名覆盖）
        var candidate = Path.Combine(directory, $"{baseName}.{extension}");
        var index = 0;
        while (File.Exists(candidate))
        {
            index++;
            candidate = Path.Combine(directory, $"{baseName}-{index}.{extension}");
        }
        return (candidate, null);
    }

    /// <summary>转码结束后的落地动作：成功校验 + 替换模式的源文件删除。</summary>
    public static void FinalizeOutput(string finalPath, string? tempPath, string sourcePath, TranscodeOutputMode mode)
    {
        if (!File.Exists(tempPath ?? finalPath))
        {
            throw new InvalidOperationException("ffmpeg 退出码为 0 但输出文件不存在");
        }

        if (tempPath is not null)
        {
            var length = new FileInfo(tempPath).Length;
            if (length <= 0)
            {
                try { File.Delete(tempPath); } catch { }
                throw new InvalidOperationException("输出文件为空，已放弃替换源文件");
            }
            File.Move(tempPath, finalPath, overwrite: true);
        }

        if (mode == TranscodeOutputMode.Replace
            && !string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(finalPath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            File.Delete(sourcePath);
        }
    }

    /// <summary>失败 / 取消后的临时文件清理。</summary>
    public static void CleanupTemp(string? tempPath)
    {
        if (tempPath is null)
        {
            return;
        }
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch
        {
            // 清理失败不影响状态落库
        }
    }
}

/// <summary>媒体文件扩展名全集（监听规则 / 文件夹扫描未指定过滤时使用）。</summary>
public static class MediaExtensions
{
    public static readonly HashSet<string> Default = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".flv", ".wmv", ".ts", ".m2ts", ".mts", ".webm",
        ".mpg", ".mpeg", ".m4v", ".3gp", ".vob", ".rmvb", ".rm",
        ".mp3", ".flac", ".wav", ".aac", ".m4a", ".ogg", ".wma", ".ape", ".opus",
    };

    /// <summary>解析用户输入的扩展名过滤（逗号/分号分隔，自动补点）；为空返回 null 表示使用默认全集。</summary>
    public static HashSet<string>? Parse(string? patterns)
    {
        var parts = (patterns ?? string.Empty)
            .Split([',', ';', '，', '；'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.StartsWith('.') ? p : "." + p)
            .Where(p => p.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return parts.Length == 0 ? null : new HashSet<string>(parts, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>监听 / 扫描时必须跳过的临时与下载中文件。</summary>
    public static bool IsTemporaryFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.Contains(OutputPathPlanner.TempMarker, StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".download", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith('~');
    }

    /// <summary>递归 / 非递归遍历目录文件（访问受限目录跳过而非中断）。</summary>
    public static IEnumerable<(string Path, long Size)> WalkFiles(string root, bool recursive)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            string[] files;
            try
            {
                files = Directory.GetFiles(current);
            }
            catch (Exception)
            {
                continue;
            }
            foreach (var file in files)
            {
                long size = 0;
                try
                {
                    size = new FileInfo(file).Length;
                }
                catch
                {
                    continue;
                }
                yield return (file, size);
            }

            if (!recursive)
            {
                continue;
            }
            string[] directories;
            try
            {
                directories = Directory.GetDirectories(current);
            }
            catch
            {
                continue;
            }
            foreach (var dir in directories)
            {
                stack.Push(dir);
            }
        }
    }
}
