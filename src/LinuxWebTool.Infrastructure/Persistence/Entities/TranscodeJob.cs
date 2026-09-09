using SqlSugar;

namespace LinuxWebTool.Infrastructure.Persistence.Entities;

/// <summary>一次转码任务（文件粒度）。执行由 TranscodeQueueService 后台驱动，进度节流回写。</summary>
[SugarTable("transcode_job")]
public class TranscodeJob
{
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [SugarColumn(ColumnDataType = "nvarchar(1000)")]
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>输出目标路径（运行时规划后回写；替换模式为最终路径，实际先写 .lwt-tmp 临时文件）。</summary>
    [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(1000)")]
    public string? OutputPath { get; set; }

    [SugarColumn(IsNullable = true)]
    public Guid? PresetId { get; set; }

    [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(100)")]
    public string? PresetName { get; set; }

    /// <summary>自定义 ffmpeg 参数（与预设互斥优先）。</summary>
    [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(2000)")]
    public string? CustomArgs { get; set; }

    /// <summary>是否为完整命令模式：true=CustomArgs 是完整 ffmpeg 命令（含 -i/输出路径），系统不注入 -progress/输出规划/硬件上下文。</summary>
    public bool IsFullCommand { get; set; }

    /// <summary>是否请求使用硬件加速（提交时标记，默认开启）。</summary>
    public bool UseHardwareAccel { get; set; } = true;

    /// <summary>用户指定的硬件后端：auto / nvenc / qsv / vaapi / v4l2m2m；auto = 按硬件信号自动排优。</summary>
    [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(20)")]
    public string? HardwareBackend { get; set; } = "auto";

    /// <summary>实际是否使用了硬件编码器（运行时判定回写；false=回退软件编码）。</summary>
    public bool UsedHardwareAccel { get; set; }

    /// <summary>实际执行的完整 ffmpeg 命令行（运行时记录，供任务队列回显）。</summary>
    [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(2000)")]
    public string? CommandLine { get; set; }

    /// <summary>回退原因（请求硬件加速但实际用软件编码时写此字段，如"预设为软件编码器"/"硬件编码器不可用，已回退软件编码"）。</summary>
    [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(500)")]
    public string? FallbackReason { get; set; }

    /// <summary>回退前本应执行的硬件加速命令（当预设指定硬件编码器但因环境不支持回退时记录，供任务队列对比）。</summary>
    [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(2000)")]
    public string? FallbackFromCommand { get; set; }

    /// <summary>输出目录；为空 = 输出到源文件所在目录。</summary>
    [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(1000)")]
    public string? OutputDir { get; set; }

    /// <summary>输出容器 / 扩展名（自定义参数模式在提交时指定；预设模式运行时从预设读取）。</summary>
    [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(20)")]
    public string? OutputContainer { get; set; }

    /// <summary>输出模式：0=替换 1=并存。</summary>
    public int OutputMode { get; set; }

    /// <summary>触发来源：0=手动 1=监听规则。</summary>
    public int Trigger { get; set; }

    [SugarColumn(IsNullable = true)]
    public Guid? WatchRuleId { get; set; }

    /// <summary>0=排队 1=转码中 2=成功 3=失败 4=取消 5=中断。</summary>
    public int Status { get; set; }

    /// <summary>进度 0~100（ffprobe 时长已知时按输出时间推进）。</summary>
    public double Progress { get; set; }

    /// <summary>实时速度文本，如 "x1.53"。</summary>
    [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(50)")]
    public string? SpeedText { get; set; }

    [SugarColumn(IsNullable = true)]
    public long? DurationMs { get; set; }

    /// <summary>失败 / 中断原因摘要（ffmpeg 日志尾部）。</summary>
    [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(2000)")]
    public string? ErrorOutput { get; set; }

    /// <summary>ffmpeg 完整日志文件路径（data/logs/transcode/&lt;jobId&gt;.log）。</summary>
    [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(500)")]
    public string? LogFile { get; set; }

    [SugarColumn(IsNullable = true)]
    public long? SourceSizeBytes { get; set; }

    [SugarColumn(IsNullable = true)]
    public long? OutputSizeBytes { get; set; }

    public DateTime QueueTime { get; set; } = DateTime.Now;

    [SugarColumn(IsNullable = true)]
    public DateTime? StartTime { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? EndTime { get; set; }

    public DateTime CreateTime { get; set; } = DateTime.Now;

    public DateTime UpdateTime { get; set; } = DateTime.Now;
}
