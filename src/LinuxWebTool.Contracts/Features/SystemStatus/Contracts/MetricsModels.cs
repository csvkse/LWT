namespace LinuxWebTool.Contracts.Features.SystemStatus.Contracts;

/// <summary>应用服务性能指标与追踪度量快照。</summary>
public sealed record AppMetricsSnapshot
{
    public long TotalRequests { get; init; }
    public int ActiveRequests { get; init; }
    public long Status2xx { get; init; }
    public long Status3xx { get; init; }
    public long Status4xx { get; init; }
    public long Status5xx { get; init; }
    public double AverageDurationMs { get; init; }
    public double P50DurationMs { get; init; }
    public double P95DurationMs { get; init; }
    public double MaxDurationMs { get; init; }
    public int ActiveTerminalSessions { get; init; }
    public long UptimeSeconds { get; init; }
}
