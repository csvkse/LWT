namespace LinuxWebTool.Contracts.Features.Plugins.Contracts;

public sealed record PluginAuthDecision
{
    public bool IsAllowed { get; init; }
    public int StatusCode { get; init; } = 200;
    public string? IdentityName { get; init; }
    public string? AuthType { get; init; }
    public string? FailureReason { get; init; }
    public string? ChallengeHeader { get; init; }

    public static PluginAuthDecision Allowed(string identityName, string authType) =>
        new() { IsAllowed = true, StatusCode = 200, IdentityName = identityName, AuthType = authType };

    public static PluginAuthDecision Denied(int statusCode, string reason, string? challenge = null) =>
        new() { IsAllowed = false, StatusCode = statusCode, FailureReason = reason, ChallengeHeader = challenge };
}

public sealed record PluginPortCheckResult
{
    public int Port { get; init; }
    public bool IsAvailable { get; init; }
    public string? OccupiedBy { get; init; }
    public int RecommendedPort { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed record PluginPortSwitchRequest
{
    public int NewPort { get; init; }
    public bool RestartIfRunning { get; init; } = true;
}

public sealed record PluginPortSwitchResult
{
    public bool Success { get; init; }
    public int PreviousPort { get; init; }
    public int NewPort { get; init; }
    public bool WasRestarted { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed record PluginEventPublishRequest
{
    public required string EventType { get; init; }
    public object? Payload { get; init; }
}

public sealed record PluginRevisionAliasRequest
{
    public required string Alias { get; init; }
    public required string RevisionName { get; init; }
}
