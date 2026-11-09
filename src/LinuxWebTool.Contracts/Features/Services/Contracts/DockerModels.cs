namespace LinuxWebTool.Contracts.Features.Services.Contracts;

/// <summary>
/// Docker 容器端口映射。
/// </summary>
public sealed record DockerPortMapping
{
    public int? PrivatePort { get; init; }
    public int? PublicPort { get; init; }
    public string? Type { get; init; } // tcp, udp
    public string? Ip { get; init; }
}

/// <summary>
/// Docker 容器摘要信息。
/// </summary>
public sealed record DockerContainerSummary
{
    public required string Id { get; init; }
    public IReadOnlyList<string> Names { get; init; } = [];
    public required string Image { get; init; }
    public string? ImageId { get; init; }
    public string? Command { get; init; }
    public long Created { get; init; }
    public required string State { get; init; } // running, exited, paused, restarting
    public string? Status { get; init; } // e.g. "Up 2 hours", "Exited (0) 5 minutes ago"
    public IReadOnlyList<DockerPortMapping> Ports { get; init; } = [];
}

/// <summary>
/// Docker 容器操作动作类型。
/// </summary>
public enum DockerContainerAction
{
    Start = 1,
    Stop = 2,
    Restart = 3,
    Pause = 4,
    Unpause = 5,
    Kill = 6,
    Remove = 7
}

/// <summary>
/// Docker 容器操作请求。
/// </summary>
public sealed record DockerContainerActionRequest
{
    public required string ContainerId { get; init; }
    public required DockerContainerAction Action { get; init; }
    public bool RemoveVolumes { get; init; }
    public bool Force { get; init; }
}

/// <summary>
/// Docker 镜像摘要信息。
/// </summary>
public sealed record DockerImageSummary
{
    public required string Id { get; init; }
    public IReadOnlyList<string> RepoTags { get; init; } = [];
    public long Size { get; init; }
    public long Created { get; init; }
}

/// <summary>
/// Docker 日志请求。
/// </summary>
public sealed record DockerLogsRequest
{
    public required string ContainerId { get; init; }
    public int Tail { get; init; } = 200;
    public bool Timestamps { get; init; }
}

/// <summary>
/// Docker 空间清理请求。
/// </summary>
public sealed record DockerPruneRequest
{
    public string PruneType { get; init; } = "builder"; // "builder", "images", "containers", "system"
    public bool All { get; init; } = true;
    public bool Volumes { get; init; } = false;
}

/// <summary>
/// Docker 清理执行结果。
/// </summary>
public sealed record DockerPruneResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public string Output { get; init; } = "";
    public long ReclaimedBytes { get; init; }
    public string ReclaimedHuman { get; init; } = "";
}

/// <summary>
/// Docker 磁盘占用分类指标。
/// </summary>
public sealed record DockerDiskUsageCategory
{
    public int TotalCount { get; init; }
    public int ActiveCount { get; init; }
    public long TotalBytes { get; init; }
    public long ReclaimableBytes { get; init; }
}

/// <summary>
/// Docker 全系统磁盘占用概览。
/// </summary>
public sealed record DockerDiskUsageSummary
{
    public DockerDiskUsageCategory Images { get; init; } = new();
    public DockerDiskUsageCategory Containers { get; init; } = new();
    public DockerDiskUsageCategory LocalVolumes { get; init; } = new();
    public DockerDiskUsageCategory BuildCache { get; init; } = new();
    public long TotalReclaimableBytes => Images.ReclaimableBytes + Containers.ReclaimableBytes + LocalVolumes.ReclaimableBytes + BuildCache.ReclaimableBytes;
}

/// <summary>
/// Docker 容器命令执行请求。
/// </summary>
public sealed record DockerContainerExecRequest
{
    public required string Command { get; init; }
    public string? WorkingDir { get; init; }
    public string? User { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
}

/// <summary>
/// Docker 容器命令执行结果。
/// </summary>
public sealed record DockerContainerExecResult
{
    public bool Success { get; init; }
    public int ExitCode { get; init; }
    public string Output { get; init; } = "";
    public string Error { get; init; } = "";
    public long DurationMs { get; init; }
}

/// <summary>
/// Docker 容器底层拓扑与运行态详细信息。
/// </summary>
public sealed record DockerContainerInspect
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";
    public int Pid { get; init; }
    public bool Running { get; init; }
    public bool Paused { get; init; }
    public bool Restarting { get; init; }
    public string? WorkingDir { get; init; }
    public string? Image { get; init; }
    public string? ImageId { get; init; }
    public string? MergedDir { get; init; }
    public string? UpperDir { get; init; }
}


