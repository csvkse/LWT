namespace LinuxWebTool.Contracts.Models;

/// <summary>转码任务状态。</summary>
public enum TranscodeJobStatus
{
    /// <summary>排队中。</summary>
    Queued = 0,
    /// <summary>转码中。</summary>
    Running = 1,
    /// <summary>成功。</summary>
    Success = 2,
    /// <summary>失败。</summary>
    Failed = 3,
    /// <summary>已取消。</summary>
    Cancelled = 4,
    /// <summary>已中断（应用重启 / 进程被杀）。</summary>
    Interrupted = 5,
}

/// <summary>输出模式。</summary>
public enum TranscodeOutputMode
{
    /// <summary>替换：转码成功后删除源文件（先写临时文件，成功校验后才替换，失败原件不动）。</summary>
    Replace = 0,
    /// <summary>并存：保留源文件，输出为同名新扩展名，重名自动加序号。</summary>
    Coexist = 1,
}

/// <summary>监听扫描方式。</summary>
public enum WatchScanMode
{
    /// <summary>轮询：定期扫描对比文件大小快照（网络挂载盘 / SMB 可靠，默认）。</summary>
    Polling = 0,
    /// <summary>文件系统事件：inotify 实时感知（仅本地磁盘可靠）。</summary>
    FileSystem = 1,
}

/// <summary>任务触发来源。</summary>
public enum TranscodeTrigger
{
    /// <summary>手动提交（页面 / API）。</summary>
    Manual = 0,
    /// <summary>监听规则自动触发。</summary>
    Watch = 1,
}

/// <summary>一次性转码提交请求：SourcePath 为文件时提交单任务；为文件夹时按扩展名过滤批量入队。</summary>
public sealed record TranscodeSubmitRequest
{
    public required string SourcePath { get; init; }

    /// <summary>转码预设（与 CustomArgs 至少一项）。</summary>
    public Guid? PresetId { get; init; }

    /// <summary>自定义 ffmpeg 参数（{input}/{output} 占位符之外的部分，如 -c:v libx264 -crf 20）。</summary>
    public string? CustomArgs { get; init; }

    /// <summary>输出容器 / 扩展名；使用自定义参数（无预设）时必填，默认 mp4。</summary>
    public string? OutputContainer { get; init; }

    public TranscodeOutputMode OutputMode { get; init; } = TranscodeOutputMode.Coexist;

    /// <summary>源为文件夹时的扩展名过滤（逗号分隔，含点，如 .mkv,.avi）；为空 = 内置媒体扩展名全集。</summary>
    public string? FilePatterns { get; init; }

    /// <summary>源为文件夹时是否包含子目录。</summary>
    public bool Recursive { get; init; }

    /// <summary>输出目录；为空时输出到源文件所在目录。</summary>
    public string? OutputDir { get; init; }

    /// <summary>是否使用硬件加速（默认开启；运行时探测不支持则回退软件编码，并记录实际结果）。</summary>
    public bool UseHardwareAccel { get; init; } = true;

    /// <summary>指定硬件后端：auto / nvenc / qsv / vaapi / v4l2m2m；auto = 按硬件信号自动排优（默认）。</summary>
    public string? HardwareBackend { get; init; } = "auto";
}

/// <summary>保存转码预设请求。</summary>
public sealed record SavePresetRequest
{
    public required string Name { get; init; }

    /// <summary>目标容器 / 输出扩展名：mp4、mkv、mp3…</summary>
    public required string Container { get; init; }

    /// <summary>视频编解码：libx264 / libx265 / copy；空 = 去视频（纯音频输出）。</summary>
    public string? VideoCodec { get; init; }

    /// <summary>视频质量 CRF（0~51，越小越清晰体积越大）；copy / 去视频时忽略。</summary>
    public int? VideoQuality { get; init; }

    /// <summary>音频编解码：aac / libmp3lame / copy；空 = 去音频。</summary>
    public string? AudioCodec { get; init; }

    /// <summary>音频码率，如 128k、192k。</summary>
    public string? AudioBitrate { get; init; }

    /// <summary>额外 ffmpeg 参数（引号感知拆分后原样附加在输出文件之前）。</summary>
    public string? ExtraArgs { get; init; }

    public string? Description { get; init; }
}

/// <summary>转码预设导入条目（用于预设导出/导入 JSON，排除 Id/CreateTime/UpdateTime 等运行时字段）。</summary>
public sealed record PresetImportItem
{
    public required string Name { get; init; }

    /// <summary>目标容器 / 输出扩展名：mp4、mkv、mp3…</summary>
    public required string Container { get; init; }

    public string? VideoCodec { get; init; }

    /// <summary>视频质量 CRF（0~51）。</summary>
    public int? VideoQuality { get; init; }

    public string? AudioCodec { get; init; }

    public string? AudioBitrate { get; init; }

    public string? ExtraArgs { get; init; }

    public string? Description { get; init; }

    /// <summary>是否内置预设：导入时按文件还原（同名已存在则跳过，不覆盖）。</summary>
    public bool IsBuiltin { get; init; }
}

/// <summary>保存监听规则请求：监听文件夹并自动转码新增文件。</summary>
public sealed record SaveWatchRuleRequest
{
    public required string Name { get; init; }

    /// <summary>监听目录（可为 SMB 挂载路径）。</summary>
    public required string WatchPath { get; init; }

    /// <summary>扩展名过滤（逗号分隔，含点，如 .mkv,.avi）；为空 = 内置媒体扩展名全集。</summary>
    public string? FilePatterns { get; init; }

    public required Guid PresetId { get; init; }

    public TranscodeOutputMode OutputMode { get; init; } = TranscodeOutputMode.Coexist;

    public bool Recursive { get; init; }

    /// <summary>扫描方式：0=轮询（网络盘友好，默认） 1=文件系统事件（本地盘实时）。</summary>
    public WatchScanMode Mode { get; init; } = WatchScanMode.Polling;

    /// <summary>轮询间隔秒数（轮询模式，最小 30）。</summary>
    public int PollSeconds { get; init; } = 300;

    public bool Enabled { get; init; } = true;

    /// <summary>是否使用硬件加速（默认开启；运行时探测不支持则回退软件编码）。</summary>
    public bool UseHardwareAccel { get; init; } = true;

    /// <summary>指定硬件后端：auto / nvenc / qsv / vaapi / v4l2m2m；auto = 按硬件信号自动排优（默认）。</summary>
    public string? HardwareBackend { get; init; } = "auto";
}

/// <summary>ffmpeg / ffprobe 检测结果。</summary>
public sealed record FfmpegDetection
{
    public bool Available { get; init; }
    public string FfmpegPath { get; init; } = string.Empty;
    public string FfprobePath { get; init; } = string.Empty;
    /// <summary>ffmpeg -version 首行，如 "ffmpeg version 6.1.1 ..."。</summary>
    public string Version { get; init; } = string.Empty;
    /// <summary>不可用时的安装 / 配置指引。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>支持的解码硬件加速方式（ffmpeg -hwaccels），如 cuda / vaapi / qsv / videotoolbox。</summary>
    public List<string> HardwareAccels { get; init; } = [];

    /// <summary>可用的硬件视频编码器（ffmpeg -encoders 过滤 nvenc / vaapi / qsv / videotoolbox），如 h264_nvenc / hevc_vaapi。</summary>
    public List<string> HwEncoders { get; init; } = [];

    /// <summary>设备节点真实存在、且对应编码器在 -encoders 里也有的后端（已双重校验，可直接用于选择）。
    /// 仅含 nvenc / qsv / vaapi / v4l2m2m 等；空 = 无可用硬件后端（将回退软件编码）。</summary>
    public List<string> HwBackends { get; init; } = [];

    /// <summary>探测到的 GPU 厂商（nvidia / intel / amd），用于提示展示；未探测到为 null。</summary>
    public string? GpuVendor { get; init; }
}
