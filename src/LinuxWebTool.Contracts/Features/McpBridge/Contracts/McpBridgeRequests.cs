namespace LinuxWebTool.Contracts.Features.McpBridge.Contracts;

public sealed record SaveTunnelRequest
{
    public string? TunnelId { get; init; }
    public string SecretAction { get; init; } = "keep"; // keep, replace, clear
    public string? TunnelKey { get; init; }
}

public sealed record SaveServerRequest
{
    public required string Name { get; init; }
    public required string Transport { get; init; } // stdio, http, sse
    public string? Command { get; init; }
    public IReadOnlyList<string>? Arguments { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? Url { get; init; }
    public string SecretAction { get; init; } = "keep"; // keep, replace, clear
    public string? SecretValue { get; init; }
    public bool Enabled { get; init; } = true;
}

public sealed record SaveSelectionRequest
{
    public ulong ExpectedRevision { get; init; }
    public required IReadOnlyList<string> SelectedExportedNames { get; init; }
}

public sealed record ApplyConfigRequest
{
    public ulong ExpectedRevision { get; init; }
}

public sealed record McpBridgeMutationResult
{
    public bool Success { get; init; }
    public ulong Revision { get; init; }
    public string? Message { get; init; }
}

public sealed record McpBridgeOperationResult
{
    public bool Success { get; init; }
    public string? Message { get; init; }
    public string? JobId { get; init; }
}

public sealed record McpBridgeServersResult
{
    public IReadOnlyList<McpServerDto> Servers { get; init; } = [];
}

public sealed record McpBridgeProbeResult
{
    public bool Success { get; init; }
    public string Status { get; init; } = "unknown";
    public int ToolCount { get; init; }
    public string? Message { get; init; }
}

public sealed record McpBridgeDiscoveryResult
{
    public bool Success { get; init; }
    public int DiscoveredCount { get; init; }
    public string? JobId { get; init; }
    public string? Message { get; init; }
}

public sealed record McpBridgeToolsPage
{
    public IReadOnlyList<McpToolDto> Tools { get; init; } = [];
    public string? NextCursor { get; init; }
    public ulong SnapshotRevision { get; init; }
}

public sealed record McpBridgeApplyResult
{
    public bool Success { get; init; }
    public ulong ActiveRevision { get; init; }
    public int ActiveToolCount { get; init; }
    public string? Message { get; init; }
}

public sealed record McpBridgeAuditPage
{
    public IReadOnlyList<McpBridgeAuditItem> Items { get; init; } = [];
    public string? NextCursor { get; init; }
}
