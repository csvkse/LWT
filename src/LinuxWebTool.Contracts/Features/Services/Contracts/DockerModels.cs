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
