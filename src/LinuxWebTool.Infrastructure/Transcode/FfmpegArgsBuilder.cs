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
    // 硬件编码器后端后缀集合：用于从编码器名判断硬件加速类型。含 d3d11va（Windows 专用）以与 FfmpegLocator 对齐。
    private static readonly string[] HwBackends = ["nvenc", "vaapi", "qsv", "v4l2m2m", "videotoolbox", "amf", "mfx", "d3d11va"];

    /// <summary>
    /// 硬件加速上下文：UseHardwareAccel=用户是否请求；AvailableHwEncoders=当前环境探测到的可用硬件编码器；
    /// PreferredBackend=用户指定的后端（auto/nvenc/qsv/vaapi/v4l2m2m，空=auto 自动排优）；
    /// ReadyHwBackends=设备就绪且编码器存在的后端集合（无硬门槛，仅用于排序优先级）。
    /// </summary>
    public sealed record HwEncodeContext(bool UseHardwareAccel, IReadOnlyList<string> AvailableHwEncoders,
        string? PreferredBackend = null, IReadOnlyList<string>? ReadyHwBackends = null);

    /// <summary>构造结果：args=首选执行的命令行参数（不含 ffmpeg 可执行文件名），UsedHardwareAccel=Args 是否为硬件命令。</summary>
    /// <param name="FallbackReason">回退原因：请求硬件加速但实际用软件编码时的说明文本；未回退为 null。</param>
    /// <param name="FallbackArgs">首选(硬件)命令失败后，应改用的替代参数段（软件）；无替代为 null。</param>
    /// <param name="IntendedHwArgs">意图执行的硬件参数段（用于显示"回退前硬件命令"）。</param>
    public sealed record BuildResult(List<string> Args, bool UsedHardwareAccel,
        string? FallbackReason = null, IReadOnlyList<string>? FallbackArgs = null,
        IReadOnlyList<string>? IntendedHwArgs = null);

    public static List<string> Build(string input, string output, TranscodePreset? preset, string? customArgs)
        => BuildWithHw(input, output, preset, customArgs, new HwEncodeContext(false, [])).Args;

    public static BuildResult BuildWithHw(string input, string output, TranscodePreset? preset, string? customArgs,
        HwEncodeContext? hwContext)
    {
        var hw = hwContext ?? new HwEncodeContext(false, []);
        var args = new List<string> { "-hide_banner", "-y", "-i", input };
        var usedHardware = false;
        string? fallbackReason = null;
        List<string>? fallbackArgs = null;
        List<string>? intendedHwArgs = null;

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
            args.AddRange(buildResult.Item1);
            usedHardware = buildResult.Item2;
            fallbackReason = buildResult.Item3;
            fallbackArgs = buildResult.Item4;
            intendedHwArgs = buildResult.Item5;
        }

        args.Add("-progress");
        args.Add("pipe:1");
        args.Add("-nostats");
        args.Add(output);
        return new BuildResult(args, usedHardware, fallbackReason, fallbackArgs, intendedHwArgs);
    }

    /// <summary>硬件编码器 → 对应软件编码器（环境不支持硬编时回退用）。</summary>
    private static readonly Dictionary<string, string> HwToSoftware = new(StringComparer.OrdinalIgnoreCase)
    {
        ["h264"] = "libx264",
        ["hevc"] = "libx265",
        ["av1"] = "libaom-av1",
        ["vp8"] = "libvpx",
        ["vp9"] = "libvpx-vp9",
        ["mjpeg"] = "mjpeg",
        ["mpeg2"] = "mpeg2video",
        ["mpeg4"] = "mpeg4",
        ["h263"] = "h263p",
    };

    /// <summary>软件视频编码器 → 视频家族（用于请求加速时查找同家族的可用硬件编码器）。</summary>
    private static readonly Dictionary<string, string> SoftwareFamily = new(StringComparer.OrdinalIgnoreCase)
    {
        ["libx264"] = "h264",
        ["libx265"] = "hevc",
        ["libvpx"] = "vp8",
        ["libvpx-vp9"] = "vp9",
        ["libaom-av1"] = "av1",
        ["libsvtav1"] = "av1",
    };

    /// <summary>软件→硬件自动映射的后端可候选清单（全部）。</summary>
    private static readonly string[] BackendPriority = ["vaapi", "nvenc", "qsv", "v4l2m2m", "mfx", "amf", "videotoolbox"];

    /// <summary>
    /// 计算后端的尝试顺序：手动指定后端排最前；否则按硬件就绪优先级（readyBackends 中的顺序）排列，
    /// 最后补上未就绪的默认候选（保证编辑器的候选不丢）。就绪的排前。
    /// </summary>
    private static IEnumerable<string> OrderBackends(string? preferred, IReadOnlyList<string>? ready)
    {
        var result = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferred) && !preferred.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            // 手动指定：指定后端唯一候选，成功后直接返回；不可用则回退软件（由调用方决定）
            yield return preferred.Trim();
            yield break;
        }

        // auto：按硬件就绪信号 + 默认优先级
        if (ready is not null)
        {
            foreach (var b in ready)
            {
                if (!result.Contains(b, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(b);
                }
            }
        }
        foreach (var b in BackendPriority)
        {
            if (!result.Contains(b, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(b);
            }
        }
        foreach (var b in result)
        {
            yield return b;
        }
    }

    /// <summary>
    /// 软件预设 + 请求加速时：从可用硬件编码器里找同家族硬件编码器。
    /// 手动指定后端（preferred）时优先该后端，且该后端须设备就绪；auto 时按 OrderBackends 顺序选首个设备就绪者。
    /// 返回 (硬编编码器 + 后端)；无可用返回 null。
    /// </summary>
    private static (string Codec, string Backend)? ResolveSoftwareHw(string softwareCodec,
        IReadOnlyList<string> available, string? preferred, IReadOnlyList<string>? ready)
    {
        if (!SoftwareFamily.TryGetValue(softwareCodec, out var family))
        {
            return null;
        }
        foreach (var backend in OrderBackends(preferred, ready))
        {
            var candidate = $"{family}_{backend}";
            if (available.Contains(candidate, StringComparer.OrdinalIgnoreCase) && HwDeviceExists(backend))
            {
                return (candidate, backend);
            }
        }
        return null;
    }

    /// <summary>判断硬件编码器对应的后端设备是否真实存在（避免映射到"编译支持但无设备"的编码器导致启动失败）。</summary>
    private static bool HwDeviceExists(string? backend)
    {
        var path = backend?.ToLowerInvariant();
        try
        {
            return path switch
            {
                "vaapi" => File.Exists("/dev/dri/renderD128") || File.Exists("/dev/dri/renderD129"),
                "qsv" or "mfx" => Directory.Exists("/dev/dri"),
                "v4l2m2m" => Directory.Exists("/dev") && Directory.EnumerateFiles("/dev", "video*").Any(),
                "nvenc" => File.Exists("/dev/nvidia0") || File.Exists("/dev/nvidiactl"),
                "videotoolbox" => OperatingSystem.IsMacOS(),
                "amf" => false,
                _ => false,
            };
        }
        catch
        {
            // 探测设备目录权限受限时视为不存在（安全降级为软件编码）
            return false;
        }
    }

    /// <summary>
    /// 预设参数构造。产出两套视频参数段：
    /// Args=首选命令；FallbackArgs=硬件启动失败时的软件兜底；IntendedHwArgs=意图执行的硬件命令（用于展示）。
    /// 请求硬件加速时：预设为硬件编码器（设备存在）直接硬编；预设为软件编码器则自动映射到同家族可用硬件编码器；
    /// 无可用硬件设备 / 未请求加速时保持软件编码并记录回退原因。
    /// </summary>
    private static (List<string> Args, bool UsedHardwareAccel, string? FallbackReason, List<string>? FallbackArgs, List<string>? IntendedHwArgs)
        BuildPresetTokens(TranscodePreset preset, HwEncodeContext hw)
    {
        var video = (preset.VideoCodec ?? string.Empty).Trim();
        var audio = (preset.AudioCodec ?? string.Empty).Trim();

        var hardwareBackend = DetectHardwareBackend(video);
        var isHwCodec = hardwareBackend is not null;

        // 软件编码器（硬件预设映射回等价软件；软件预设原样）
        string? softwareCodec = isHwCodec
            ? (HwToSoftware.TryGetValue(video[..(video.Length - hardwareBackend!.Length - 1)], out var sw) ? sw : null)
            : video;

        // 选定的硬件方案（编码器 + 后端）；null = 本次不启用硬编
        (string Codec, string Backend)? hwPlan = null;
        var preferred = string.IsNullOrWhiteSpace(hw.PreferredBackend) ? "auto" : hw.PreferredBackend.Trim();
        if (hw.UseHardwareAccel)
        {
            if (isHwCodec)
            {
                // 预设即硬件编码器：需在探测列表内 且 设备存在；手动指定后端时还需匹配指定后端
                var backendMatch = preferred.Equals("auto", StringComparison.OrdinalIgnoreCase)
                    || preferred.Equals(hardwareBackend, StringComparison.OrdinalIgnoreCase);
                if (backendMatch
                    && hw.AvailableHwEncoders.Contains(video, StringComparer.OrdinalIgnoreCase)
                    && HwDeviceExists(hardwareBackend))
                {
                    hwPlan = (video, hardwareBackend!);
                }
            }
            else
            {
                // 软件预设 → 自动映射到同家族可用硬件编码器（支持手动指定后端 + 硬件就绪优先级）
                hwPlan = ResolveSoftwareHw(video, hw.AvailableHwEncoders, preferred, hw.ReadyHwBackends);
            }
        }

        // 分段：软件段（真实软件编码器）与硬件段（意图的硬编）
        var softwareVideo = new List<string>();
        var hardwareVideo = new List<string>();
        string? fallbackReason = null;

        if (video.Length == 0)
        {
            softwareVideo.Add("-vn"); // 纯音频
        }
        else if (video.Equals("copy", StringComparison.OrdinalIgnoreCase))
        {
            softwareVideo.AddRange(["-c:v", "copy"]);
        }
        else if (hwPlan is { } plan)
        {
            // 硬编：前置上下文 + -c:v <hardware> + -qp
            hardwareVideo = BuildHwContextArgs(plan.Backend).ToList();
            hardwareVideo.AddRange(["-c:v", plan.Codec]);
            if (preset.VideoQuality is >= 0 and <= 51)
            {
                hardwareVideo.AddRange(["-qp", preset.VideoQuality.Value.ToString()]);
            }
            // 软件兜底段（若硬件启动失败时重跑）
            if (softwareCodec is not null)
            {
                softwareVideo.AddRange(["-c:v", softwareCodec]);
                if (preset.VideoQuality is >= 0 and <= 51)
                {
                    softwareVideo.AddRange(["-crf", preset.VideoQuality.Value.ToString()]);
                }
            }
            if (isHwCodec)
            {
                fallbackReason = $"硬件编码器 {video} 已启用";
            }
            else
            {
                fallbackReason = $"预设 {video} 为软件编码器，已自动映射为硬件编码器 {plan.Codec}";
            }
        }
        else if (softwareCodec is not null)
        {
            // 保持软件编码
            softwareVideo.AddRange(["-c:v", softwareCodec]);
            if (preset.VideoQuality is >= 0 and <= 51)
            {
                softwareVideo.AddRange(["-crf", preset.VideoQuality.Value.ToString()]);
            }
            if (isHwCodec)
            {
                // 硬件预设但环境不支持 / 未请求加速 → 已映射软件
                fallbackReason = $"硬件编码器 {video} 不可用，已回退软件编码 {softwareCodec}";
            }
            else if (hw.UseHardwareAccel)
            {
                var pref = string.IsNullOrWhiteSpace(hw.PreferredBackend) ? "auto" : hw.PreferredBackend.Trim();
                fallbackReason = pref.Equals("auto", StringComparison.OrdinalIgnoreCase)
                    ? $"请求硬件加速，但预设视频编码器 {video} 无可用的同家族硬件编码器，保持软件编码"
                    : $"请求使用 {pref} 硬件后端，但该后端在此环境不可用（无对应设备或编码器），已回退软件编码";
            }
            // 记录意图的硬件命令（供展示"回退前硬件命令"）
            if (isHwCodec)
            {
                hardwareVideo = BuildHwContextArgs(hardwareBackend!).ToList();
                hardwareVideo.AddRange(["-c:v", video]);
                if (preset.VideoQuality is >= 0 and <= 51)
                {
                    hardwareVideo.AddRange(["-qp", preset.VideoQuality.Value.ToString()]);
                }
            }
        }

        // 音频 + 容器 + 额外参数（软件与硬件共用；额外参数在硬件路径需剥离软件专属项）
        var tail = new List<string>();
        if (audio.Length == 0)
        {
            tail.Add("-an");
        }
        else if (audio.Equals("copy", StringComparison.OrdinalIgnoreCase))
        {
            tail.AddRange(["-c:a", "copy"]);
        }
        else
        {
            tail.AddRange(["-c:a", audio]);
            if (!string.IsNullOrWhiteSpace(preset.AudioBitrate))
            {
                tail.AddRange(["-b:a", preset.AudioBitrate.Trim()]);
            }
        }

        var isCopy = video.Equals("copy", StringComparison.OrdinalIgnoreCase);
        if (preset.Container.Trim().Equals("mp4", StringComparison.OrdinalIgnoreCase) && !isCopy)
        {
            tail.AddRange(["-movflags", "+faststart"]);
        }

        // 软件路径用完整 ExtraArgs；硬件路径剥离软件专属项（-preset/-tag:v/-crf 等）。
        var fullExtra = string.IsNullOrWhiteSpace(preset.ExtraArgs)
            ? new List<string>()
            : ShellArgumentParser.Split(preset.ExtraArgs).ToList();
        var hwExtra = FilterHwCompatibleArgs(fullExtra);
        var hwTail = tail.Concat(hwExtra).ToList();
        var swTail = tail.Concat(fullExtra).ToList();

        var hwPrimary = hwPlan is not null;
        var args = (hwPrimary ? hardwareVideo.Concat(hwTail) : softwareVideo.Concat(swTail)).ToList();
        // 硬件启动失败时的软件兜底段（软件路径保留全部 ExtraArgs）
        var fallbackArgs = hwPrimary && softwareVideo.Count > 0 ? softwareVideo.Concat(swTail).ToList() : null;
        var intendedHwArgs = hardwareVideo.Count > 0 ? hardwareVideo.Concat(hwTail).ToList() : null;
        return (args, hwPrimary, fallbackReason, fallbackArgs, intendedHwArgs);
    }

    /// <summary>软件编码专属参数项（硬件编码路径需剥离）。键为参数名（不含前导 '-', 小写）。</summary>
    private static readonly HashSet<string> SoftwareOnlyArgs = new(StringComparer.OrdinalIgnoreCase)
    {
        "preset",           // libx264/libx265 的层级，硬编不支持
        "crf",              // 软件码率控制，硬编用 -qp
        "tag:v",            // MP4 tag（如 hvc1），硬编由容器自动处理
        "profile:v",        // 软件 profile，硬编可自动
        "tune",             // x264 tune，硬编不支持
        "maxrate",
        "bufsize",
        "x264-params",      // x264 专属
        "x265-params",      // x265 专属
        "sc_threshold",
        "psy-rd",
        "me_method",
        "subq",
        "keyint_min",
    };

    /// <summary>过滤出硬件编码可接受的参数：跳过软件专属项（及其参数值），保留通用容器/像素/码率等参数。</summary>
    private static List<string> FilterHwCompatibleArgs(IReadOnlyList<string> tokens)
    {
        var result = new List<string>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Length == 0 || !token.StartsWith('-'))
            {
                // 不带 '-' 的值 token 保留（无法判断归属，交给 ffmpeg）
                result.Add(token);
                continue;
            }
            var name = token.TrimStart('-').ToLowerInvariant();
            if (SoftwareOnlyArgs.Contains(name))
            {
                // 若该选项带值（下一 token 不以 '-' 开头），跳过一个值 token
                if (i + 1 < tokens.Count && !tokens[i + 1].StartsWith('-'))
                {
                    i++;
                }
                continue;
            }
            result.Add(token);
        }
        return result;
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
