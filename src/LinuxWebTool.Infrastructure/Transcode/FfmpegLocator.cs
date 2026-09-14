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

        // 探测硬件加速：解码加速方式（-hwaccels）、硬件编码器（-encoders 过滤）、GPU 元数据，
        // 最后用真实编码探针确认驱动初始化和编码链路可用。
        // 任何失败静默降级为空。
        var hwEncoders = await RunHwEncodersAsync(options.FfmpegPath);
        var driDevices = DiscoverDriDevices();
        detection = detection with
        {
            HardwareAccels = await RunFfmpegListAsync(options.FfmpegPath, "-hide_banner -hwaccels"),
            HwEncoders = hwEncoders,
        };
        var detectionWithVendor = detection with { GpuVendor = DetectGpuVendor(driDevices) };
        return await BuildHardwareDetectionAsync(detectionWithVendor, options.FfmpegPath, RunCommandAsync, () => driDevices);
    }

    /// <summary>
    /// 用真实编码探针过滤硬件后端：编码器存在仅是候选条件，探针成功才进入 HwBackends；
    /// DRI 后端会记录探针成功的 render 设备，真实转码必须复用同一设备。
    /// 空 = 无可用硬件后端（将回退软件编码）。当前覆盖 nvenc / qsv / vaapi。
    /// </summary>
    internal sealed record CommandResult(int ExitCode, string Stdout, string Stderr);
    internal sealed record DriDevice(string Path, string? Vendor = null, string? Driver = null);

    internal static async Task<FfmpegDetection> BuildHardwareDetectionAsync(
        FfmpegDetection detection,
        string ffmpeg,
        Func<string, string, CancellationToken, Task<CommandResult>> commandRunner,
        Func<IReadOnlyList<DriDevice>>? driDeviceProvider = null)
    {
        var ready = new List<string>();
        var failures = new List<string>();
        var backendDevices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        DriDevice? selectedDevice = null;
        string? selectedVendor = null;
        var driDevices = driDeviceProvider?.Invoke() ?? DiscoverDriDevices();
        var candidates = new[]
        {
            ("nvenc", "nvenc"),
            ("vaapi", "_vaapi"),
            ("qsv", "_qsv"),
        };

        foreach (var (backend, keyword) in candidates)
        {
            if (!detection.HwEncoders.Any(c => c.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            IEnumerable<DriDevice?> deviceCandidates = backend.ToLowerInvariant() switch
            {
                "vaapi" => driDevices.Where(d => !string.Equals(d.Vendor, "nvidia", StringComparison.OrdinalIgnoreCase)),
                "qsv" => driDevices.Where(d => string.IsNullOrWhiteSpace(d.Vendor)
                    || string.Equals(d.Vendor, "intel", StringComparison.OrdinalIgnoreCase)),
                _ => Enumerable.Repeat<DriDevice?>(null, 1),
            };

            CommandResult? lastFailure = null;
            var probedAnyDevice = false;
            foreach (var device in deviceCandidates)
            {
                probedAnyDevice = true;
                var result = await commandRunner(
                    ffmpeg,
                    BuildEncodeProbeArguments(backend, device?.Path),
                    CancellationToken.None);
                if (result.ExitCode == 0)
                {
                    if (!ready.Contains(backend, StringComparer.OrdinalIgnoreCase))
                    {
                        ready.Add(backend);
                    }
                    if (device is not null)
                    {
                        backendDevices.TryAdd(backend, device.Path);
                        selectedDevice ??= device;
                        selectedVendor ??= device.Vendor;
                    }
                    lastFailure = null;
                    break;
                }

                lastFailure = result;
            }

            if (!probedAnyDevice && backend is "vaapi" or "qsv")
            {
                failures.Add($"{backend}: 未发现可用的 DRI 渲染设备");
            }
            else if (lastFailure is { } failure)
            {
                failures.Add($"{backend}: {SelectProbeError(failure)}");
            }
        }

        string? gpuName = null;
        string? gpuDriverVersion = null;
        if (ready.Contains("nvenc", StringComparer.OrdinalIgnoreCase) ||
            string.Equals(detection.GpuVendor, "nvidia", StringComparison.OrdinalIgnoreCase))
        {
            var nvidia = await commandRunner(
                "nvidia-smi",
                "--query-gpu=name,driver_version --format=csv,noheader,nounits",
                CancellationToken.None);
            if (nvidia.ExitCode == 0)
            {
                (gpuName, gpuDriverVersion) = ParseNvidiaGpu(nvidia.Stdout);
            }
        }
        else if (!string.IsNullOrWhiteSpace(detection.GpuVendor) || selectedDevice is not null)
        {
            var vainfoDevice = selectedDevice?.Path ?? driDevices.FirstOrDefault()?.Path ?? "/dev/dri/renderD128";
            var vainfo = await commandRunner("vainfo", $"-display drm -device {vainfoDevice}", CancellationToken.None);
            if (vainfo.ExitCode == 0)
            {
                (gpuName, gpuDriverVersion) = ParseVainfoGpu(vainfo.Stdout);
            }
        }

        return detection with
        {
            HwBackends = ready,
            GpuDevice = selectedDevice?.Path,
            GpuVendor = selectedVendor ?? detection.GpuVendor,
            GpuName = gpuName,
            GpuDriverVersion = gpuDriverVersion,
            HwBackendDevices = backendDevices,
            HardwareReady = ready.Count > 0,
            HardwareMessage = BuildHardwareMessage(ready, failures),
        };
    }

    private static string BuildEncodeProbeArguments(string backend, string? devicePath) => backend switch
    {
        "vaapi" => $"-hide_banner -v error -init_hw_device vaapi=va:{devicePath ?? "/dev/dri/renderD128"} -filter_hw_device va "
            + "-f lavfi -i testsrc2=size=256x256:rate=10 -t 0.1 "
            + "-vf format=nv12,hwupload -c:v h264_vaapi -f null -",
        "qsv" => $"-hide_banner -v error -init_hw_device qsv=hw,child_device={devicePath ?? "/dev/dri/renderD128"} "
            + "-c:v h264_qsv -f null -",
        "nvenc" => "-hide_banner -v error -f lavfi -i testsrc2=size=256x256:rate=10 -t 0.1 "
            + "-c:v h264_nvenc -f null -",
        _ => string.Empty,
    };

    private static string BuildHardwareMessage(IReadOnlyList<string> ready, IReadOnlyList<string> failures)
    {
        if (ready.Count > 0)
        {
            return $"已通过真实编码探针：{string.Join("、", ready)}";
        }
        if (failures.Count > 0)
        {
            return $"硬件编码器已编译，但编码探针失败（可能是驱动异常、设备不可访问或权限不足）：{string.Join("；", failures)}";
        }
        return "未检测到可用的硬件视频编码器，将使用软件编码。";
    }

    private static string SelectProbeError(CommandResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;
        if (string.IsNullOrWhiteSpace(text))
        {
            return $"ffmpeg 退出码 {result.ExitCode}";
        }

        var details = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Take(2);
        var value = string.Join(" / ", details);
        return value.Length > 180 ? value[..177] + "..." : value;
    }

    private static (string? Name, string? DriverVersion) ParseNvidiaGpu(string stdout)
    {
        var line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(line))
        {
            return (null, null);
        }

        var values = line.Split(',', StringSplitOptions.TrimEntries);
        return (values.Length > 0 ? values[0] : null, values.Length > 1 ? values[1] : null);
    }

    private static (string? Name, string? DriverVersion) ParseVainfoGpu(string stdout)
    {
        var line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Contains("Driver version:", StringComparison.OrdinalIgnoreCase));
        if (line is null)
        {
            return (null, null);
        }

        var index = line.IndexOf("Driver version:", StringComparison.OrdinalIgnoreCase);
        var value = line[(index + "Driver version:".Length)..].Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return (null, null);
        }

        // Mesa 常见格式：Mesa Gallium driver 24.0.9 for AMD Radeon Graphics (renoir, LLVM 15.0.7)
        if (value.StartsWith("Mesa Gallium driver ", StringComparison.OrdinalIgnoreCase) &&
            value.Contains(" for ", StringComparison.OrdinalIgnoreCase))
        {
            var forIndex = value.IndexOf(" for ", StringComparison.OrdinalIgnoreCase);
            return (value[(forIndex + 5)..].Trim(), value[..forIndex].Trim());
        }

        // iHD 常见格式：Intel iHD driver for Intel(R) Gen Graphics - 24.1.0
        var forMarker = " for ";
        if (value.Contains(forMarker, StringComparison.OrdinalIgnoreCase))
        {
            var startIndex = value.IndexOf(forMarker, StringComparison.OrdinalIgnoreCase);
            var endIndex = value.IndexOf(" - ", startIndex + forMarker.Length, StringComparison.Ordinal);
            if (endIndex > startIndex)
            {
                var name = value[(startIndex + forMarker.Length)..endIndex].Trim();
                return (string.IsNullOrWhiteSpace(name) ? null : name, value);
            }
        }

        return (null, value);
    }

    /// <summary>
    /// 枚举所有 DRI 渲染节点，并尽量通过 sysfs 读取 PCI vendor 与内核驱动。
    /// 枚举失败时安全返回空列表；此时不会误报某个具体设备可用。
    /// </summary>
    internal static List<DriDevice> DiscoverDriDevices()
    {
        try
        {
            if (!Directory.Exists("/dev/dri"))
            {
                return [];
            }

            return Directory.EnumerateFiles("/dev/dri", "renderD*")
                .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                .Select(path => new DriDevice(path, ReadDriVendor(path), ReadDriDriver(path)))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string? ReadDriVendor(string devicePath)
    {
        var name = Path.GetFileName(devicePath);
        return NormalizePciVendor(ReadSmallTextFile($"/sys/class/drm/{name}/device/vendor"));
    }

    private static string? ReadDriDriver(string devicePath)
    {
        var name = Path.GetFileName(devicePath);
        foreach (var line in (ReadSmallTextFile($"/sys/class/drm/{name}/device/uevent") ?? string.Empty).Split('\n'))
        {
            if (line.StartsWith("DRIVER=", StringComparison.OrdinalIgnoreCase))
            {
                return line["DRIVER=".Length..].Trim();
            }
        }
        return null;
    }

    private static string? ReadSmallTextFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizePciVendor(string? vendor)
    {
        if (string.IsNullOrWhiteSpace(vendor))
        {
            return null;
        }

        return vendor.Trim().ToLowerInvariant() switch
        {
            "0x8086" => "intel",
            "0x1002" or "0x1022" => "amd",
            "0x10de" => "nvidia",
            _ => vendor.Trim(),
        };
    }

    /// <summary>探测 GPU 厂商：nvidia / intel / amd；未探测到（无设备节点且无内核模块）返回 null。</summary>
    private static string? DetectGpuVendor(IReadOnlyList<DriDevice> driDevices)
    {
        if (HasNvidiaDevice())
        {
            return "nvidia";
        }
        if (driDevices.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.Vendor)) is { } device)
        {
            return device.Vendor;
        }
        if (driDevices.Count > 0)
        {
            // sysfs 不可读时不再假定厂商；vendor 为 null 的设备仍会跑 VAAPI 探针，
            // 但 QSV 保守地不启用，避免 AMD 环境出现误导性的 MFX 错误。
            if (IsModuleLoaded("i915"))
            {
                return "intel";
            }
            if (IsModuleLoaded("amdgpu"))
            {
                return "amd";
            }
            return null;
        }
        return null;
    }

    private static bool HasNvidiaDevice()
        => File.Exists("/dev/nvidia0") || File.Exists("/dev/nvidiactl") || Directory.Exists("/proc/driver/nvidia");

    private static bool HasDriBackend()
        => Directory.Exists("/dev/dri");

    /// <summary>运行硬件探测命令。探针应在页面刷新内完成，超时从常规探测的 10 秒压缩到 5 秒。</summary>
    private static async Task<CommandResult> RunCommandAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
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
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new CommandResult(-1, string.Empty, "探测超时（5 秒）");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new CommandResult(process.HasExited ? process.ExitCode : -1, stdout, stderr);
        }
        catch (Exception ex)
        {
            return new CommandResult(-1, string.Empty, ex.Message);
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
