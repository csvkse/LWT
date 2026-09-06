namespace LinuxWebTool.Contracts.Models;

/// <summary>主机静态信息。</summary>
public sealed record HostInfo
{
    public string HostName { get; init; } = string.Empty;
    /// <summary>操作系统名称（发行版 / Windows 版本）。</summary>
    public string OsName { get; init; } = string.Empty;
    public string KernelVersion { get; init; } = string.Empty;
    public string Architecture { get; init; } = string.Empty;
    /// <summary>运行时长（秒）。</summary>
    public long UptimeSeconds { get; init; }
}

/// <summary>CPU 状态。</summary>
public sealed record CpuStatus
{
    public string ModelName { get; init; } = string.Empty;
    /// <summary>逻辑核心数。</summary>
    public int CoreCount { get; init; }
    /// <summary>使用率（0~100）。</summary>
    public double UsagePercent { get; init; }
    public double Load1 { get; init; }
    public double Load5 { get; init; }
    public double Load15 { get; init; }
}

/// <summary>内存状态（字节）。</summary>
public sealed record MemoryStatus
{
    public long TotalBytes { get; init; }
    public long UsedBytes { get; init; }
    public long AvailableBytes { get; init; }
    /// <summary>使用率（0~100）。</summary>
    public double UsagePercent { get; init; }
    public long SwapTotalBytes { get; init; }
    public long SwapUsedBytes { get; init; }
}

/// <summary>磁盘挂载点状态（字节）。</summary>
public sealed record DiskStatus
{
    public string Mount { get; init; } = string.Empty;
    public string FileSystem { get; init; } = string.Empty;
    public long TotalBytes { get; init; }
    public long UsedBytes { get; init; }
    public long FreeBytes { get; init; }
    /// <summary>使用率（0~100）。</summary>
    public double UsagePercent { get; init; }
}

/// <summary>网卡速率状态。</summary>
public sealed record NetworkStatus
{
    public string Name { get; init; } = string.Empty;
    /// <summary>采样窗口内平均发送速率（字节/秒）。</summary>
    public long SentBytesPerSec { get; init; }
    public long RecvBytesPerSec { get; init; }
    public long TotalSentBytes { get; init; }
    public long TotalRecvBytes { get; init; }
}

/// <summary>进程状态。</summary>
public sealed record ProcessStatus
{
    public int Pid { get; init; }
    public string Name { get; init; } = string.Empty;
    /// <summary>CPU 占用（0~100，单进程相对整机）。</summary>
    public double CpuPercent { get; init; }
    /// <summary>内存占用（0~100）。</summary>
    public double MemPercent { get; init; }
    public long MemBytes { get; init; }
}

/// <summary>一次系统状态快照（即时全量）。</summary>
public sealed record SystemStatusResult
{
    public HostInfo Host { get; init; } = new();
    public CpuStatus Cpu { get; init; } = new();
    public MemoryStatus Memory { get; init; } = new();
    public IReadOnlyList<DiskStatus> Disks { get; init; } = [];
    public IReadOnlyList<NetworkStatus> Networks { get; init; } = [];
    public IReadOnlyList<ProcessStatus> TopCpuProcesses { get; init; } = [];
    public IReadOnlyList<ProcessStatus> TopMemProcesses { get; init; } = [];
    public DateTime SampledAt { get; init; }
}

/// <summary>历史曲线序列点（与快照表列对应）。</summary>
public sealed record StatusSnapshotPoint
{
    public DateTime Time { get; init; }
    /// <summary>CPU 使用率（0~100）。</summary>
    public double CpuUsage { get; init; }
    public double Load1 { get; init; }
    /// <summary>内存使用率（0~100）。</summary>
    public double MemUsage { get; init; }
    /// <summary>根分区使用率（0~100）。</summary>
    public double DiskRootUsage { get; init; }
    /// <summary>全网卡发送速率（字节/秒）。</summary>
    public long NetSentBps { get; init; }
    /// <summary>全网卡接收速率（字节/秒）。</summary>
    public long NetRecvBps { get; init; }
}
