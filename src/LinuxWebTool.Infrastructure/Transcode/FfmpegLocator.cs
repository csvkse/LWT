using System.Diagnostics;
using LinuxWebTool.Contracts.Models;

namespace LinuxWebTool.Infrastructure.Transcode;

/// <summary>
/// ffmpeg / ffprobe 可用性检测与解析。默认走 PATH；可经 Media:FfmpegPath 配置绝对路径。
/// 检测结果缓存 60 秒，避免页面轮询反复拉起子进程。
/// </summary>
public sealed class FfmpegLocator(TranscodeOptions options)
{
    private (DateTime Time, FfmpegDetection Result)? _cache;

    public async Task<FfmpegDetection> DetectAsync()
    {
        if (_cache is { } cache && (DateTime.Now - cache.Time).TotalSeconds < 60)
        {
            return cache.Result;
        }

        var result = await DetectCoreAsync();
        _cache = (DateTime.Now, result);
        return result;
    }

    private async Task<FfmpegDetection> DetectCoreAsync()
    {
        var (exitCode, stdout) = await RunVersionAsync(options.FfmpegPath, "ffmpeg");
        if (exitCode != 0)
        {
            return new FfmpegDetection
            {
                Available = false,
                FfmpegPath = options.FfmpegPath,
                FfprobePath = options.FfprobePath,
                Message = "未检测到 ffmpeg。Docker 镜像已内置；桌面部署请安装 ffmpeg（apt install ffmpeg / brew / winget）"
                    + "或配置 Media__FfmpegPath 指向可执行文件",
            };
        }

        // ffprobe 用于时长探测；缺失只影响进度百分比，不阻断转码
        var (probeExit, probeOut) = await RunVersionAsync(options.FfprobePath, "ffprobe");
        var version = stdout.Split('\n').FirstOrDefault()?.Trim() ?? "ffmpeg";

        var detection = new FfmpegDetection
        {
            Available = true,
            FfmpegPath = options.FfmpegPath,
            FfprobePath = probeExit == 0 ? options.FfprobePath : string.Empty,
            Version = version,
            Message = probeExit != 0 ? "ffprobe 不可用（进度将无法显示百分比，转码本身不受影响）" : string.Empty,
        };

        // 探测硬件加速：解码加速方式（-hwaccels）与硬件编码器（-encoders 过滤）。任何失败静默降级为空。
        detection = detection with
        {
            HardwareAccels = await RunFfmpegListAsync(options.FfmpegPath, "-hide_banner -hwaccels"),
            HwEncoders = await RunHwEncodersAsync(options.FfmpegPath),
        };
        return detection;
    }

    private static readonly HashSet<string> HwEncoderKeywords =
        new(StringComparer.OrdinalIgnoreCase) { "nvenc", "vaapi", "qsv", "videotoolbox", "amf", "d3d11va", "v4l2m2m", "mfx" };

    /// <summary>解析 ffmpeg -hwaccels 输出（首行含 "Hardware acceleration methods:"，之后每行一个方法名）。</summary>
    private static async Task<List<string>> RunFfmpegListAsync(string ffmpeg, string arguments)
    {
        var (exitCode, stdout) = await RunArgsAsync(ffmpeg, arguments);
        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            return [];
        }
        return stdout.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("Hardware acceleration", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>解析 ffmpeg -encoders 输出，仅保留含硬件关键字（nvenc/vaapi/qsv 等）的编码器名（V... 行末字段）。</summary>
    private static async Task<List<string>> RunHwEncodersAsync(string ffmpeg)
    {
        var (exitCode, stdout) = await RunArgsAsync(ffmpeg, "-hide_banner -encoders");
        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            return [];
        }
        var result = new List<string>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            // 编码器行以 V...（视频）打头，格式 " V....D h264_nvenc  NVIDIA NVENC H.264 encoder"。
            if (trimmed.Length < 8 || (trimmed[0] != 'V' && trimmed[0] != 'v'))
            {
                continue;
            }
            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }
            var codec = parts[1];
            if (HwEncoderKeywords.Any(k => codec.Contains(k, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(codec);
            }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>以任意参数运行 ffmpeg，返回 (exitCode, stdout)。复用超时 kill 逻辑，任何异常降级 (-1, "")。</summary>
    private static async Task<(int ExitCode, string Stdout)> RunArgsAsync(string fileName, string arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            process.Start();
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (-1, string.Empty);
            }
            var stdout = await process.StandardOutput.ReadToEndAsync();
            return (process.HasExited ? process.ExitCode : -1, stdout);
        }
        catch
        {
            return (-1, string.Empty);
        }
    }

    private static async Task<(int ExitCode, string Stdout)> RunVersionAsync(string fileName, string toolName)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            process.Start();
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (-1, string.Empty);
            }
            var stdout = await process.StandardOutput.ReadToEndAsync();
            return (process.HasExited ? process.ExitCode : -1, stdout);
        }
        catch
        {
            return (-1, string.Empty);
        }
    }
}
