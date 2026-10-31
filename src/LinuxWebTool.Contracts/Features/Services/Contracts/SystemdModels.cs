namespace LinuxWebTool.Contracts.Features.Services.Contracts;

/// <summary>
/// Systemd 单元简要条目。
/// </summary>
public sealed record SystemdUnitItem
{
    public required string Unit { get; init; }
    public string? LoadState { get; init; }
    public string? ActiveState { get; init; }
    public string? SubState { get; init; }
    public string? UnitFileState { get; init; }
    public string? Description { get; init; }
    public int? MainPid { get; init; }
    public string Category { get; init; } = "other";
    public string Scope { get; init; } = "system";
}

/// <summary>
/// Systemd 单元过滤与查询条件。
/// </summary>
public sealed record SystemdQueryCriteria
{
    public string? FilterKeyword { get; init; }
    public string? ActiveState { get; init; } // active, inactive, failed, all
    public string? UnitType { get; init; } // service, timer, socket, etc.
    public string? Category { get; init; } // all, app, system, network, storage, other
    public string? Scope { get; init; } // all, system, user
}

/// <summary>
/// Systemd 日志查询请求。
/// </summary>
public sealed record SystemdJournalRequest
{
    public required string UnitName { get; init; }
    public int Lines { get; init; } = 100;
    public string? Since { get; init; }
}

/// <summary>
/// Systemd 日志查询结果。
/// </summary>
public sealed record SystemdJournalResult
{
    public required string UnitName { get; init; }
    public IReadOnlyList<string> Lines { get; init; } = [];
}
