using System.Text.Json;

namespace LinuxWebTool.Contracts.Features.McpBridge.Contracts;

/// <summary>
/// MCP Bridge 整体运行与连接状态 DTO。
/// </summary>
public sealed record McpBridgeStatusDto
{
    public string? WorkerRunId { get; init; }
    public bool ProcessAlive { get; init; }
    public bool AdminReady { get; init; }
    public string TunnelState { get; init; } = "unconfigured"; // unconfigured, connected, connecting, auth_failed, stopped, error
    public int BackendCount { get; init; }
    public ulong DraftRevision { get; init; }
    public ulong DesiredRevision { get; init; }
    public ulong ActiveRevision { get; init; }
    public bool RecoveryRequired { get; init; }
    public string? LastError { get; init; }
}

/// <summary>
/// 纳管的第三方 MCP 服务定义与运行状态。
/// </summary>
public sealed record McpServerDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Transport { get; init; } // stdio, http, sse
    public string? Command { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public string? Url { get; init; }
    public bool HasSecret { get; init; }
    public bool Enabled { get; init; } = true;
    public int ToolCount { get; init; }
    public string Status { get; init; } = "idle"; // idle, connected, error
    public string? Error { get; init; }
}

/// <summary>
/// 发现并可选勾选暴露的 MCP 工具定义。
/// </summary>
public sealed record McpToolDto
{
    public required string ExportedName { get; init; }
    public required string ServerId { get; init; }
    public required string OriginalName { get; init; }
    public string? Description { get; init; }
    public JsonElement? InputSchema { get; init; }
    public bool Selected { get; init; }
    public string SchemaDigest { get; init; } = "";
}

/// <summary>
/// MCP 审计事件条目。
/// </summary>
public sealed record McpBridgeAuditItem
{
    public DateTimeOffset Timestamp { get; init; }
    public string RunId { get; init; } = "";
    public ulong ConfigRevision { get; init; }
    public string ServerId { get; init; } = "";
    public string ExportedToolName { get; init; } = "";
    public long DurationMs { get; init; }
    public string Status { get; init; } = "";
    public string? ErrorCode { get; init; }
    public long ResultSizeBytes { get; init; }
    public bool EffectUncertain { get; init; }
}
