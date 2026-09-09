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

        // 探测硬件加速：解码加速方式（-hwaccels）、硬件编码器（-encoders 过滤）、及"设备就绪且编码器存在"的后端。
        // 任何失败静默降级为空。
        var hwEncoders = await RunHwEncodersAsync(options.FfmpegPath);
        detection = detection with
        {
            HardwareAccels = await RunFfmpegListAsync(options.FfmpegPath, "-hide_banner -hwaccels"),
            HwEncoders = hwEncoders,
            HwBackends = DetectReadyHwBackends(hwEncoders),
            GpuVendor = DetectGpuVendor(),
        };
        return detection;
    }

    /// <summary>
    /// 探测"设备节点真实存在 且 对应编码器在 -encoders 里也有"的后端集合（双重校验）。
    /// 空 = 无可用硬件后端（将回退软件编码）。仅识别 nvenc / qsv / vaapi / v4l2m2m / mfx。
    /// </summary>
    private static List<string> DetectReadyHwBackends(List<string> hwEncoders)
    {
        var ready = new List<string>();
        if (HasNvidiaDevice() && hwEncoders.Any(c => c.Contains("nvenc", StringComparison.OrdinalIgnoreCase)))
        {
            ready.Add("nvenc");
        }
        if (HasDriBackend() && hwEncoders.Any(c => c.Contains("_vaapi", StringComparison.OrdinalIgnoreCase)))
        {
            ready.Add("vaapi");
        }
        if (HasDriBackend() && hwEncoders.Any(c => c.Contains("_qsv", StringComparison.OrdinalIgnoreCase)))
        {
            ready.Add("qsv");
        }
        if (HasVideoDevice() && hwEncoders.Any(c => c.Contains("v4l2m2m", StringComparison.OrdinalIgnoreCase)))
        {
            ready.Add("v4l2m2m");
        }
        return ready;
    }

    /// <summary>探测 GPU 厂商：nvidia / intel / amd；未探测到（无设备节点且无内核模块）返回 null。</summary>
    private static string? DetectGpuVendor()
    {
        if (HasNvidiaDevice())
        {
            return "nvidia";
        }
        if (HasDriBackend())
        {
            // /dev/dri 存在，结合内核模块区分 Intel（i915）/ AMD（amdgpu）
            if (IsModuleLoaded("i915"))
            {
                return "intel";
            }
            if (IsModuleLoaded("amdgpu"))
            {
                return "amd";
            }
            return "intel"; // 有 dri 但模块不可读时，兜底视为 intel（最常见场景）
        }
        return null;
    }

    private static bool HasNvidiaDevice()
        => File.Exists("/dev/nvidia0") || File.Exists("/dev/nvidiactl") || Directory.Exists("/proc/driver/nvidia");

    private static bool HasDriBackend()
        => Directory.Exists("/dev/dri");

    private static bool HasVideoDevice()
    {
        try
        {
            return Directory.Exists("/dev") && Directory.EnumerateFiles("/dev", "video*").Any();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>检查内核模块是否加载（读 /proc/modules 首列），任何异常返回 false。</summary>
    private static bool IsModuleLoaded(string name)
    {
        try
        {
            if (!File.Exists("/proc/modules"))
            {
                return false;
            }
            foreach (var line in File.ReadLines("/proc/modules"))
            {
                var first = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (string.Equals(first, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
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
