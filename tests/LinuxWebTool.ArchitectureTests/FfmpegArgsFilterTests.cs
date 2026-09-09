using LinuxWebTool.Infrastructure.Persistence.Entities;
using LinuxWebTool.Infrastructure.Transcode;
using Xunit;

namespace LinuxWebTool.ArchitectureTests;

/// <summary>
/// FfmpegArgsBuilder 硬件路径剥离软件专属参数的回归测试：
/// 软预设下的命令必须保留 ExtraArgs（-preset 等），被剥离逻辑仅作用于硬件路径。
/// </summary>
public class FfmpegArgsFilterTests
{
    private static TranscodePreset SoftPreset() => new()
    {
        Name = "MP4 H.264 通用",
        Container = "mp4",
        VideoCodec = "libx264",
        VideoQuality = 23,
        AudioCodec = "aac",
        AudioBitrate = "128k",
        ExtraArgs = "-preset medium -tag:v hvc1",
    };

    [Fact]
    public void Software_path_keeps_extra_args()
    {
        // 不使用硬件加速：软件路径必须保留 -preset medium / -tag:v hvc1
        var result = FfmpegArgsBuilder.BuildWithHw(
            "/in.mp4", "/out.mp4", SoftPreset(), null,
            new FfmpegArgsBuilder.HwEncodeContext(false, []));

        var cmd = string.Join(' ', result.Args);
        Assert.Contains("-c:v libx264", cmd);
        Assert.Contains("-preset medium", cmd);   // 软件路径保留
        Assert.Contains("-tag:v hvc1", cmd);      // 软件路径保留
        Assert.False(result.UsedHardwareAccel);
    }

    [Fact]
    public void Hw_compatible_filter_strips_software_only_args()
    {
        // 反射调用私有 FilterHwCompatibleArgs，验证剥离软件专属项（-preset/-tag:v），保留通用项
        var method = typeof(FfmpegArgsBuilder).GetMethod("FilterHwCompatibleArgs",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var tokens = new List<string> { "-preset", "medium", "-c:a", "aac", "-tag:v", "hvc1", "-b:a", "96k" };
        var filtered = (List<string>)method!.Invoke(null, new object[] { tokens })!;
        var cmd = string.Join(' ', filtered);

        Assert.DoesNotContain("preset", cmd);
        Assert.DoesNotContain("-tag:v", cmd);
        Assert.DoesNotContain("hvc1", cmd);
        // 保留通用项
        Assert.Contains("-c:a aac", cmd);
        Assert.Contains("-b:a 96k", cmd);
    }
}
