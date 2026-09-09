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

    [Fact]
    public void Hw_global_args_and_filter_args_are_split()
    {
        // 反射调用私有 BuildHwGlobalArgs / BuildHwFilterArgs，确认 -vaapi_device/-hwaccel 归入全局段、-vf 归入滤镜段
        var global = typeof(FfmpegArgsBuilder).GetMethod("BuildHwGlobalArgs",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var filter = typeof(FfmpegArgsBuilder).GetMethod("BuildHwFilterArgs",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(global);
        Assert.NotNull(filter);

        var g = (IEnumerable<string>)global!.Invoke(null, new object[] { "vaapi" })!;
        var f = (IEnumerable<string>)filter!.Invoke(null, new object[] { "vaapi" })!;

        var gCmd = string.Join(' ', g);
        var fCmd = string.Join(' ', f);

        Assert.Contains("-vaapi_device /dev/dri/renderD128", gCmd); // 全局段含设备
        Assert.Contains("-hwaccel vaapi", gCmd);                    // 全局段含解码加速
        Assert.DoesNotContain("-vf", gCmd);                          // 全局段不含滤镜
        Assert.Contains("-vf format=nv12,hwupload", fCmd);          // 滤镜段含 -vf
    }

    [Fact]
    public void Raw_command_is_used_as_is()
    {
        // 完整命令模式：BuildRaw 直接返回拆分后的命令，不注入 -i/-progress/-nostats/输出
        var result = FfmpegArgsBuilder.BuildRaw("ffmpeg -i /in.mp4 -c:v libx264 /out.mp4");
        var cmd = string.Join(' ', result.Args);

        Assert.Equal("-i /in.mp4 -c:v libx264 /out.mp4", cmd);
        Assert.DoesNotContain("-hide_banner", cmd);
        Assert.DoesNotContain("-progress", cmd);
        Assert.DoesNotContain("-nostats", cmd);
        Assert.False(result.UsedHardwareAccel);
        // 输入源不被强制注入
        Assert.DoesNotContain(" -i /in.mp4 ", cmd); // 由用户自写，系统不重复
    }

    [Fact]
    public void Build_args_from_preset_returns_middle_section()
    {
        // 非完整模式：预设展开为中间段参数（不含 -i/输出/-progress）
        var args = FfmpegArgsBuilder.BuildArgsFromPreset(SoftPreset(), "auto", [], [], false);
        Assert.Contains("-c:v libx264", args);
        Assert.Contains("-crf 23", args);       // 软件质量 → -crf
        Assert.Contains("-c:a aac", args);
        Assert.Contains("-b:a 128k", args);
        Assert.Contains("-movflags +faststart", args);
        Assert.DoesNotContain("-i ", args);      // 无输入
        Assert.DoesNotContain("-progress", args);// 无进度
        Assert.DoesNotContain("-nostats", args); // 无 nostats
    }

    [Fact]
    public void Build_full_command_includes_input_progress_output()
    {
        // 完整命令模式：预设展开为完整命令（含 -i/进度/输出/中间段，非硬件路径保留软件 ExtraArgs）
        var cmd = FfmpegArgsBuilder.BuildFullCommand(SoftPreset(), "/in.mp4", "/out.mp4", "auto", [], [], false);
        Assert.StartsWith("ffmpeg -y", cmd);
        Assert.Contains("-i /in.mp4", cmd);
        Assert.Contains("-c:v libx264", cmd);
        Assert.Contains("-crf 23", cmd);
        Assert.Contains("-progress pipe:1 -nostats /out.mp4", cmd);
    }

    [Fact]
    public void Build_full_command_keeps_software_extra_args_in_non_hw()
    {
        // 非硬件路径完整命令保留软件专属 ExtraArgs（-preset medium）
        var cmd = FfmpegArgsBuilder.BuildFullCommand(SoftPreset(), "/in.mp4", "/out.mp4", "auto", [], [], false);
        Assert.Contains("-preset medium", cmd);
        Assert.Contains("-tag:v hvc1", cmd);
    }
}
