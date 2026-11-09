namespace LinuxWebTool.Contracts.Features.Plugins.Contracts;

public sealed record PluginArchetype
{
    public PluginProfile Profile { get; init; } = PluginProfile.Resident;
    public PluginLifecycle Lifecycle { get; init; } = PluginLifecycle.Interpreted;
    public InterfaceProtocol Protocol { get; init; } = InterfaceProtocol.Http;
    public string? Language { get; init; }
}

public sealed record PluginLayoutSpec
{
    public string? SourceEntry { get; init; }
    public string? TargetArtifact { get; init; }
    public string StorageDir { get; init; } = "storage";
    public string RuntimeDir { get; init; } = "run";
}

public sealed record PluginBuildSpec
{
    public string? Toolchain { get; init; }
    public string? Command { get; init; }
    public string? FallbackCommand { get; init; }
    public string? WorkingDir { get; init; }
    public IReadOnlyList<string> Watch { get; init; } = [];
    public bool AutoCompileOnSave { get; init; } = true;
    public int TimeoutSeconds { get; init; } = 60;
    public IReadOnlyList<string> CleanTargets { get; init; } = [];
    public IReadOnlyList<string>? Artifacts { get; init; }
    public IReadOnlyDictionary<string, string>? Env { get; init; }
}

public sealed record PluginServiceDefinition
{
    public string? Description { get; init; }
    public IReadOnlyList<string>? Operations { get; init; }
}

public sealed record PluginInterfaceSpec
{
    public InterfaceProtocol Protocol { get; init; } = InterfaceProtocol.Http;
    public string? SocketPath { get; init; }
    public string? Endpoint { get; init; }
    public IReadOnlyList<int> PublicPorts { get; init; } = [];
    public string? HealthEndpoint { get; init; }
    public IReadOnlyDictionary<string, PluginServiceDefinition>? Services { get; init; }
}

public sealed record PluginResourceQuotaLimits
{
    public string? Cpu { get; init; }
    public string? Memory { get; init; }
    public int? Pids { get; init; }
}

public sealed record PluginResourceSpec
{
    public PluginResourceQuotaLimits? Limits { get; init; }
    public PluginResourceQuotaLimits? Requests { get; init; }
}

public sealed record PluginProbeDefinition
{
    public string Type { get; init; } = "HttpCheck"; // HttpCheck, SocketFileCheck, ExecCheck, WasmModuleCheck
    public string? Target { get; init; }
    public string? Url { get; init; }
    public int TimeoutSeconds { get; init; } = 5;
}

public sealed record PluginProbeSpec
{
    public PluginProbeDefinition? Liveness { get; init; }
    public PluginProbeDefinition? Readiness { get; init; }
}

public sealed record PluginEventsSpec
{
    public IReadOnlyList<string> Publishes { get; init; } = [];
    public IReadOnlyList<string> Subscribes { get; init; } = [];
}

public sealed record PluginBuildResult
{
    public bool Success { get; init; }
    public int ExitCode { get; init; }
    public string Output { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
    public string? TargetArtifact { get; init; }
    public IReadOnlyList<string>? Artifacts { get; init; }
    public string? ErrorMessage { get; init; }
    public double DurationMs { get; init; }
    public double ElapsedMs { get => DurationMs; init => DurationMs = value; }
}

public sealed record ServiceDescriptorDto
{
    public required string Id { get; init; }
    public string? PluginId { get; init; }
    public required string Name { get; init; }
    public PluginProfile Profile { get; init; }
    public PluginLifecycle Lifecycle { get; init; }
    public InterfaceProtocol Protocol { get; init; }
    public PluginState State { get; init; }
    public int? Port { get; init; }
    public string? SocketPath { get; init; }
    public string? HealthEndpoint { get; init; }
    public string? Endpoint { get; init; }
    public IReadOnlyList<string> Actions { get; init; } = [];
    public IReadOnlyList<string> Operations { get; init; } = [];
    public IReadOnlyList<string> PublishedEvents { get; init; } = [];
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

public sealed record PluginInvokeRequest
{
    public required string PluginId { get; init; }
    public required string ActionOrPath { get; init; }
    public string? Payload { get; init; }
    public string? Operation => ActionOrPath;
    public string? ContentType { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}

public sealed record PluginInvokeResponse
{
    public bool Success { get; init; }
    public int StatusCode { get; init; } = 200;
    public string? Output { get; init; }
    public string? Error { get; init; }
    public string? Payload { get => Output; init => Output = value; }
    public string? ContentType { get; init; }
    public double DurationMs { get; init; }
    public double LatencyMs { get => DurationMs; init => DurationMs = value; }
    public IReadOnlyList<string>? Logs { get; init; }
}
