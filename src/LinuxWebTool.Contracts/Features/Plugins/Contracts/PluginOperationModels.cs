namespace LinuxWebTool.Contracts.Features.Plugins.Contracts;

/// <summary>
/// 产物元数据与引用。
/// </summary>
public sealed record PluginArtifactRef
{
    public required string ArtifactId { get; init; }
    public required string Filename { get; init; }
    public long SizeBytes { get; init; }
    public string DownloadUrl { get; init; } = "";
    public string? ContentType { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 命令/动作调用请求模型。
/// </summary>
public sealed record CommandInvocation
{
    public required string PluginIdentity { get; init; }
    public required string Action { get; init; }
    public System.Text.Json.JsonElement Input { get; init; }
    public string? ExpectedRevision { get; init; }
    public string? IdempotencyKey { get; init; }
    public string? Caller { get; init; }
}

/// <summary>
/// 异步操作已接收响应。
/// </summary>
public sealed record OperationAccepted
{
    public required string OperationId { get; init; }
    public required string StatusUrl { get; init; }
    public string Status { get; init; } = "queued"; // queued, starting, running
    public DateTimeOffset? EstimatedDeadlineUtc { get; init; }
}

/// <summary>
/// 统一动作执行结果。
/// </summary>
public sealed record PluginActionResult
{
    public string Status { get; init; } = "succeeded"; // accepted, succeeded, failed, cancelled, timedOut, unknown
    public bool Success { get => Status == "succeeded" || Status == "accepted"; init { if (!value && Status == "succeeded") Status = "failed"; } }
    public int? ExitCode { get; init; }
    public System.Text.Json.JsonElement? Data { get; init; }
    public string? Message { get; init; }
    public string? Error { get; init; }
    public string? ErrorCode { get; init; }
    public IReadOnlyList<PluginArtifactRef> ArtifactRefs { get; init; } = [];
    public bool EffectUncertain { get; init; }
}

/// <summary>
/// 插件异步操作状态 DTO。
/// </summary>
public sealed record PluginOperationDto
{
    public required string OperationId { get; init; }
    public string Caller { get; init; } = "";
    public required string PluginIdentity { get; init; }
    public required string Action { get; init; }
    public string Status { get; init; } = "queued"; // queued, starting, running, succeeded, failed, cancelled, timedOut, unknown, interrupted
    public double? ProgressPercent { get; init; }
    public string? ProgressMessage { get; init; }
    public PluginActionResult? Result { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAtUtc { get; init; }
    public DateTimeOffset? FinishedAtUtc { get; init; }
    public bool EffectUncertain { get; init; }
    public string? IdempotencyKey { get; init; }
    public string? InputDigest { get; init; }
}
