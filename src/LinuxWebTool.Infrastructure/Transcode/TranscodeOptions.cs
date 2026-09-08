namespace LinuxWebTool.Infrastructure.Transcode;

/// <summary>转码配置（appsettings 的 Media 节；环境变量 Media__FfmpegPath 等）。</summary>
public sealed class TranscodeOptions
{
    public const string SectionName = "Media";

    /// <summary>ffmpeg 可执行文件；默认走 PATH，桌面部署需自行安装 ffmpeg 或指定绝对路径。</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>ffprobe 可执行文件（读取媒体时长，用于进度计算）。</summary>
    public string FfprobePath { get; set; } = "ffprobe";

    /// <summary>最大并发转码数（ffmpeg 极耗 CPU，默认 1）。</summary>
    public int MaxConcurrent { get; set; } = 1;

    /// <summary>单次文件夹提交的任务数上限，防止误操作产生海量任务。</summary>
    public int MaxBatchSubmit { get; set; } = 500;

    /// <summary>ffmpeg 完整日志保留天数（data/logs/transcode/&lt;jobId&gt;.log）。</summary>
    public int LogRetentionDays { get; set; } = 7;
}
