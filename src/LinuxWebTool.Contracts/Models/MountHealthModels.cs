namespace LinuxWebTool.Contracts.Models;

/// <summary>SMB 挂载运行期健康状态。</summary>
public enum MountHealthState
{
    Unknown = 0,
    Healthy = 1,
    NotMounted = 2,
    ServerUnreachable = 3,
    Stale = 4,
    Recovering = 5,
    RecoveryFailed = 6,
    Unsupported = 7,
}

/// <summary>挂载健康探测结果。按 LocalPath 缓存，容量字段由磁盘采集按需使用。</summary>
public sealed record MountHealthSnapshot
{
    public string LocalPath { get; init; } = string.Empty;
    public MountHealthState State { get; init; }
    public DateTime? LastCheckedAt { get; init; }
    public DateTime? NextAttemptAt { get; init; }
    public string? LastError { get; init; }
    public int FailureCount { get; init; }
    public int RecoveryAttemptCount { get; init; }
}
