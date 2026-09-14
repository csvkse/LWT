using LinuxWebTool.Contracts.Models;
using LinuxWebTool.Infrastructure.Transcode;
using Xunit;

namespace LinuxWebTool.ArchitectureTests;

/// <summary>
/// FfmpegLocator 硬件探测回归测试：硬件加速必须以真实编码探针为准，
/// 编码器仅被编译进 ffmpeg 不能代表驱动可用。
/// </summary>
public sealed class FfmpegLocatorTests
{
    [Fact]
    public async Task Hardware_detection_only_keeps_backends_that_pass_encode_probe()
    {
        var source = new FfmpegDetection
        {
            Available = true,
            HwEncoders = ["h264_vaapi", "h264_qsv", "h264_nvenc"],
        };

        var detection = await FfmpegLocator.BuildHardwareDetectionAsync(
            source,
            "/usr/bin/ffmpeg",
            (_, arguments, _) =>
            {
                var failed = arguments.Contains("h264_vaapi", StringComparison.Ordinal);
                return Task.FromResult(new FfmpegLocator.CommandResult(
                    failed ? 1 : 0,
                    string.Empty,
                    failed ? "VA-API driver initialization failed" : string.Empty));
            },
            () => [new FfmpegLocator.DriDevice("/dev/dri/renderD128", "intel")]);

        Assert.Equal(["nvenc", "qsv"], detection.HwBackends);
        Assert.True(detection.HardwareReady);
        Assert.Contains("真实编码探针", detection.HardwareMessage);
    }

    [Fact]
    public async Task Hardware_detection_reports_failure_when_compiled_encoder_cannot_encode()
    {
        var source = new FfmpegDetection
        {
            Available = true,
            HwEncoders = ["h264_vaapi"],
            GpuVendor = "amd",
        };

        var detection = await FfmpegLocator.BuildHardwareDetectionAsync(
            source,
            "/usr/bin/ffmpeg",
            (_, arguments, _) => Task.FromResult(new FfmpegLocator.CommandResult(
                arguments.Contains("h264_vaapi", StringComparison.Ordinal) ? 1 : 0,
                string.Empty,
                "Driver initialization failed")),
            () => [new FfmpegLocator.DriDevice("/dev/dri/renderD128", "amd")]);

        Assert.Empty(detection.HwBackends);
        Assert.False(detection.HardwareReady);
        Assert.Contains("编码探针失败", detection.HardwareMessage);
        Assert.Contains("Driver initialization failed", detection.HardwareMessage);
    }

    [Fact]
    public async Task Hardware_detection_parses_nvidia_name_and_driver_version()
    {
        var source = new FfmpegDetection
        {
            Available = true,
            HwEncoders = ["h264_nvenc"],
            GpuVendor = "nvidia",
        };

        var detection = await FfmpegLocator.BuildHardwareDetectionAsync(
            source,
            "/usr/bin/ffmpeg",
            (fileName, _, _) => Task.FromResult(new FfmpegLocator.CommandResult(
                0,
                fileName == "nvidia-smi"
                    ? "NVIDIA GeForce RTX 3060, 535.183.01"
                    : string.Empty,
                string.Empty)));

        Assert.Equal(["nvenc"], detection.HwBackends);
        Assert.Equal("NVIDIA GeForce RTX 3060", detection.GpuName);
        Assert.Equal("535.183.01", detection.GpuDriverVersion);
    }

    [Fact]
    public async Task Amd_detection_skips_qsv_and_probes_vaapi_with_nv12()
    {
        var source = new FfmpegDetection
        {
            Available = true,
            HwEncoders = ["h264_vaapi", "h264_qsv"],
            GpuVendor = "amd",
        };
        var probes = new List<(string FileName, string Arguments)>();

        var detection = await FfmpegLocator.BuildHardwareDetectionAsync(
            source,
            "/usr/bin/ffmpeg",
            (fileName, arguments, _) =>
            {
                probes.Add((fileName, arguments));
                return Task.FromResult(new FfmpegLocator.CommandResult(
                    arguments.Contains("format=nv12,hwupload", StringComparison.Ordinal) ? 0 : 1,
                    string.Empty,
                arguments.Contains("h264_qsv", StringComparison.Ordinal)
                    ? "QSV is unavailable on AMD"
                    : string.Empty));
            },
            () => [new FfmpegLocator.DriDevice("/dev/dri/renderD128", "amd")]);

        Assert.Equal(["vaapi"], detection.HwBackends);
        Assert.True(detection.HardwareReady);
        Assert.DoesNotContain(probes, probe => probe.Arguments.Contains("h264_qsv", StringComparison.Ordinal));
        Assert.Contains(probes, probe =>
            probe.Arguments.Contains("format=nv12,hwupload", StringComparison.Ordinal) &&
            probe.Arguments.Contains("h264_vaapi", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Multi_gpu_detection_maps_each_ready_backend_to_its_device()
    {
        var source = new FfmpegDetection
        {
            Available = true,
            HwEncoders = ["h264_vaapi", "h264_qsv"],
        };
        var devices = new[]
        {
            new FfmpegLocator.DriDevice("/dev/dri/renderD128", "intel"),
            new FfmpegLocator.DriDevice("/dev/dri/renderD129", "amd"),
        };

        var detection = await FfmpegLocator.BuildHardwareDetectionAsync(
            source,
            "/usr/bin/ffmpeg",
            (fileName, arguments, _) => Task.FromResult(new FfmpegLocator.CommandResult(
                fileName == "vainfo"
                    ? 0
                    : (arguments.Contains("h264_vaapi", StringComparison.Ordinal)
                        && arguments.Contains("/dev/dri/renderD129", StringComparison.Ordinal))
                    || (arguments.Contains("h264_qsv", StringComparison.Ordinal)
                        && arguments.Contains("/dev/dri/renderD128", StringComparison.Ordinal))
                        ? 0
                        : 1,
                fileName == "vainfo" ? "vainfo: Driver version: Test Driver 1.0" : string.Empty,
                string.Empty)),
            () => devices);

        Assert.Equal(["vaapi", "qsv"], detection.HwBackends);
        Assert.True(detection.HardwareReady);
        Assert.Equal("/dev/dri/renderD129", detection.GpuDevice);
        Assert.Equal("/dev/dri/renderD129", detection.HwBackendDevices["vaapi"]);
        Assert.Equal("/dev/dri/renderD128", detection.HwBackendDevices["qsv"]);
    }

    [Fact]
    public void Hardware_args_use_device_selected_by_probe()
    {
        var devices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["vaapi"] = "/dev/dri/renderD129",
        };
        var context = new FfmpegArgsBuilder.HwEncodeContext(
            true, ["h264_vaapi"], "vaapi", ["vaapi"], HwBackendDevices: devices);

        var result = FfmpegArgsBuilder.BuildWithHw(
            "/in.mp4", "/out.mp4", null, "-c:v h264_vaapi", context);

        Assert.True(result.UsedHardwareAccel);
        Assert.Contains("-vaapi_device", result.Args);
        Assert.Contains("/dev/dri/renderD129", result.Args);
        Assert.DoesNotContain("/dev/dri/renderD128", result.Args);
    }
}
