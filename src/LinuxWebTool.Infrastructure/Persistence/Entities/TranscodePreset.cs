using SqlSugar;

namespace LinuxWebTool.Infrastructure.Persistence.Entities;

/// <summary>转码预设：ffmpeg 参数的声明式组合；进阶参数走 ExtraArgs。</summary>
[SugarTable("transcode_preset")]
public class TranscodePreset
{
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [SugarColumn(ColumnDataType = "nvarchar(100)")]
    public string Name { get; set; } = string.Empty;

    /// <summary>目标容器 / 输出扩展名：mp4、mkv、mp3…</summary>
    [SugarColumn(ColumnDataType = "nvarchar(20)")]
    public string Container { get; set; } = "mp4";

    /// <summary>视频编解码：libx264 / libx265 / copy；空 = 去视频。</summary>
    [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(50)")]
    public string? VideoCodec { get; set; }

    /// <summary>视频质量 CRF（0~51）；copy / 去视频时忽略。</summary>
    [SugarColumn(IsNullable = true)]
    public int? VideoQuality { get; set; }

    /// <summary>音频编解码：aac / libmp3lame / copy；空 = 去音频。</summary>
    [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(50)")]
    public string? AudioCodec { get; set; }

    /// <summary>音频码率，如 128k。</summary>
    [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(20)")]
    public string? AudioBitrate { get; set; }

    /// <summary>额外 ffmpeg 参数（原样附加在输出文件之前）。</summary>
    [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(2000)")]
    public string? ExtraArgs { get; set; }

    [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(500)")]
    public string? Description { get; set; }

    /// <summary>内置预设（首次启动播种；可编辑可删除）。</summary>
    public bool IsBuiltin { get; set; }

    public DateTime CreateTime { get; set; } = DateTime.Now;

    public DateTime UpdateTime { get; set; } = DateTime.Now;
}
