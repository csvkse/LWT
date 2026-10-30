namespace LinuxWebTool.Contracts.Features.Services.Contracts;

/// <summary>
/// 服务管理中枢宿主环境探测画像。
/// </summary>
public sealed record ServiceEnvironmentInfo
{
    public required string OperatingSystem { get; init; }
    public required string Platform { get; init; }
    public required bool Is64Bit { get; init; }
    public required string Hostname { get; init; }
    public required string KernelVersion { get; init; }

    /// <summary>
    /// 是否在 Docker 容器内部运行。
    /// </summary>
    public bool IsDockerContainer { get; init; }

    /// <summary>
    /// 若在容器内运行，是否具备宿主特权（--privileged）与宿主 PID 命名空间（--pid=host）。
    /// </summary>
    public bool IsPrivilegedContainer { get; init; }

    /// <summary>
    /// 是否可使用 systemctl（本地原生或特权 nsenter 穿透）。
    /// </summary>
    public bool HasSystemd { get; init; }

    /// <summary>
    /// systemd 是否是通过特权容器 nsenter 穿透至宿主。
    /// </summary>
    public bool SystemdPierced { get; init; }

    /// <summary>
    /// Docker Daemon Socket 是否可用（本地或挂载的 /var/run/docker.sock）。
    /// </summary>
    public bool HasDockerSocket { get; init; }

    /// <summary>
    /// Docker Daemon 版本或探测描述。
    /// </summary>
    public string? DockerVersion { get; init; }

    /// <summary>
    /// 是否安装并检测到 OpenSSH 服务 (sshd)。
    /// </summary>
    public bool HasSshd { get; init; }

    /// <summary>
    /// 是否安装并检测到 Samba 服务 (smbd)。
    /// </summary>
    public bool HasSamba { get; init; }

    /// <summary>
    /// 网络协议栈管理类型（如 NetworkManager / Netplan / SystemdResolved / WinNet）。
    /// </summary>
    public string? NetworkStack { get; init; }

    /// <summary>
    /// 防火墙工具类型（ufw / firewalld / iptables / none）。
    /// </summary>
    public string? FirewallType { get; init; }

    /// <summary>
    /// 宿主机已安装的可用脚本运行时列表（bash, powershell, pwsh, python3, node 等）。
    /// </summary>
    public IReadOnlyList<string> InstalledRunners { get; init; } = [];
}

/// <summary>
/// 服务操作动作类型。
/// </summary>
public enum ServiceActionType
{
    Start = 1,
    Stop = 2,
    Restart = 3,
    Reload = 4,
    Enable = 5,
    Disable = 6
}

/// <summary>
/// 统一服务动作请求。
/// </summary>
public sealed record ServiceActionRequest
{
    public required string ServiceId { get; init; }
    public required ServiceActionType Action { get; init; }
}

/// <summary>
/// 统一服务操作返回结果。
/// </summary>
public sealed record ServiceActionResult
{
    public bool Success { get; init; }
    public string? Message { get; init; }
    public string? Output { get; init; }
}

public sealed record SystemdUnitFileResponse
{
    public required string Unit { get; init; }
    public required string Content { get; init; }
}

public sealed record DockerContainerLogsResponse
{
    public required string ContainerId { get; init; }
    public required string Logs { get; init; }
}

public sealed record ExtensionLogsResponse
{
    public required string ExtensionId { get; init; }
    public required string Logs { get; init; }
}
