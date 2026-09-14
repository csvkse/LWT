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
            });

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
                "Driver initialization failed")));

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
}
