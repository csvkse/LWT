namespace LinuxWebTool.Infrastructure.SystemInfo;

/// <summary>
/// 系统状态历史采样配置（appsettings 的 SystemStatus 节；环境变量 SystemStatus__SamplingIntervalSeconds 等）。
/// </summary>
public sealed class SystemStatusOptions
{
    public const string SectionName = "SystemStatus";

    /// <summary>整机快照采样间隔（秒）。</summary>
    public int SamplingIntervalSeconds { get; set; } = 60;

    /// <summary>基线采样间隔（分钟）：日常占用低频率记录。</summary>
    public int BaselineIntervalMinutes { get; set; } = 30;

    /// <summary>异常采样间隔（分钟）：任一资源越阈值后进入异常窗口的更高频率记录。</summary>
    public int AlarmIntervalMinutes { get; set; } = 10;

    /// <summary>CPU 使用率触发异常阈值（0~100）。</summary>
    public double CpuAlarmThreshold { get; set; } = 80;

    /// <summary>内存使用率触发异常阈值（0~100）。</summary>
    public double MemAlarmThreshold { get; set; } = 85;

    /// <summary>网络收发速率触发异常阈值（字节/秒）；0 表示不触网卡阈值。</summary>
    public long NetAlarmThresholdBps { get; set; } = 0;

    /// <summary>磁盘使用率触发异常阈值（0~100）；0 表示不触磁盘阈值。</summary>
    public double DiskAlarmThreshold { get; set; } = 0;

    /// <summary>进程 TOP 记录数（CPU 与内存各自）。</summary>
    public int TopProcessCount { get; set; } = 10;

    /// <summary>数据保留天数（整机 / 进程 / 磁盘 / 网络快照统一）。</summary>
    public int RetentionDays { get; set; } = 7;
}
