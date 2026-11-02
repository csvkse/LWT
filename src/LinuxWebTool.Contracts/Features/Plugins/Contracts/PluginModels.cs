namespace LinuxWebTool.Contracts.Features.Plugins.Contracts;

public enum PluginProfile
{
    Resident = 0,
    OneShot = 1
}

public enum PluginRuntime
{
    Node = 0,
    Python = 1,
    DotNet = 2,
    Bash = 3,
    PowerShell = 4,
    Native = 5,
    Go = 6
}

public enum PluginState
{
    Stopped = 0,
    Starting = 1,
    Running = 2,
    Crashed = 3,
    Backoff = 4,
    Degraded = 5,
    Disabled = 6,
    MissingRuntime = 7,
    MissingDependencies = 8
}

public sealed record PluginNetworkConfig
{
    public string Mode { get; init; } = "none"; // "none" | "path" | "port"
    public int? Port { get; init; }
    public string? PortEnv { get; init; }
    public string? PathPrefix { get; init; }
    public bool SupportHttp { get => Mode == "port" || Mode == "path" || Port > 0; init { if (value && Mode == "none") Mode = "port"; } }
    public string PortConflictPolicy { get; init; } = "avoid"; // "avoid" | "strict"

    public string AuthMode { get; init; } = "inherit"; // "inherit" | "hybrid" | "apikey" | "anonymous"
    public IReadOnlyList<string> PublicPaths { get; init; } = [];
    public bool RequireInternalSecret { get; init; } = false;
}

public sealed record PluginStorageConfig
{
    public bool EnableSqlite { get; init; }
    public bool EnableKv { get; init; }
}

public sealed record PluginSupervisionConfig
{
    public bool AutoRestart { get; init; } = true;
    public bool? AutoStart { get; init; }
    public int FlappingThresholdSec { get; init; } = 10;
    public int MaxCrashRetries { get; init; } = 5;
    public int MaxRestarts { get => MaxCrashRetries; init => MaxCrashRetries = value; }
}

public sealed record PluginUiConfig
{
    public string? Entry { get; init; }
    public string? Title { get; init; }
    public string? Icon { get; init; }
}

public sealed record PluginCustomAction
{
    public required string Action { get; init; }
    public required string Label { get; init; }
    public string? Icon { get; init; }
    public bool RequireParam { get; init; }
    public string? ParamPlaceholder { get; init; }
    public string Executor { get; init; } = "script";
    public NativeCommandDefinition? Command { get; init; }
}

public sealed record PluginConfigField
{
    public required string Field { get; init; }
    public required string Label { get; init; }
    public string Type { get; init; } = "text"; // text, password, number, boolean
    public bool Required { get; init; }
    public string? DefaultValue { get; init; }
}

public sealed record PluginDependencyProbeConfig
{
    public string Command { get; init; } = "";
    public IReadOnlyList<string> Args { get; init; } = [];
}

public sealed record PluginDependencyConfig
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool Required { get; init; }
    public bool Recommended { get; init; } = true;
    public string Description { get; init; } = "";
    public PluginDependencyProbeConfig? Probe { get; init; }
    public IReadOnlyDictionary<string, string>? InstallGuide { get; init; }
    public bool IsInstalled { get; init; }
}

[System.Text.Json.Serialization.JsonConverter(typeof(PluginDependenciesJsonConverter))]
public sealed record PluginDependencies
{
    public IReadOnlyList<string> Runners { get; init; } = [];
    public IReadOnlyList<string> Binaries { get; init; } = [];
    public IReadOnlyList<PluginDependencyConfig> Items { get; init; } = [];
}

public sealed record PluginManifest
{
    public int ManifestVersion { get; init; } = 1;
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Version { get; init; } = "1.0.0";
    public string Description { get; init; } = "";
    public string Icon { get; init; } = "🧩";
    public string Category { get; init; } = "General";
    public bool IsEnabled { get; init; } = true;
    public bool Enabled => IsEnabled;
    public string Target { get; init; } = "container";

    public PluginProfile Profile { get; init; } = PluginProfile.Resident;
    public PluginRuntime Runtime { get; init; } = PluginRuntime.Node;
    public string Entrypoint { get; init; } = "";
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    public string Source { get; init; } = "package";
    public string ProgramRef { get; init; } = "";

    public PluginNetworkConfig? Network { get; init; }
    public PluginStorageConfig? Storage { get; init; }
    public PluginSupervisionConfig? Supervision { get; init; }
    public PluginUiConfig? Ui { get; init; }
    public IReadOnlyList<PluginCustomAction> CustomActions { get; init; } = [];
    public IReadOnlyList<PluginConfigField> ConfigForm { get; init; } = [];
    public PluginDependencies? Dependencies { get; init; }
    public IReadOnlyList<PluginDependencyConfig>? OptionalDependencies { get; init; }
    public IReadOnlyDictionary<string, string>? InstallGuide { get; init; }

    public IReadOnlyList<PluginDependencyConfig> GetUnifiedDependencies()
    {
        if (OptionalDependencies != null && OptionalDependencies.Count > 0)
            return OptionalDependencies;
        if (Dependencies != null)
        {
            if (Dependencies.Items.Count > 0) return Dependencies.Items;
            var list = new List<PluginDependencyConfig>();
            foreach (var b in Dependencies.Binaries)
            {
                list.Add(new PluginDependencyConfig
                {
                    Id = b,
                    Name = b,
                    Required = true,
                    Recommended = true,
                    Description = $"系统依赖二进制工具: {b}",
                    Probe = new PluginDependencyProbeConfig { Command = b, Args = ["--version"] }
                });
            }
            return list;
        }
        return [];
    }
}

public sealed record PluginInstanceDto
{
    public required PluginManifest Manifest { get; init; }
    public string Id => Manifest.Id;
    public string Name => Manifest.Name;
    public PluginProfile Profile => Manifest.Profile;
    public PluginRuntime Runtime => Manifest.Runtime;
    public bool IsEnabled => Manifest.IsEnabled;
    public PluginState State { get; init; } = PluginState.Stopped;
    public int? AllocatedPort { get; init; }
    public bool IsPortAvoided { get; init; }
    public int? ConfiguredPort { get; init; }
    public string? PortConflictReason { get; init; }
    public int? ProcessId { get; init; }
    public string? AccessUrl { get; init; }
    public string? GatewayUrl => AccessUrl;
    public string? InternalSecret { get; init; }
    public string? StatusMessage { get; init; }
    public int CrashCount { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? LastCrashAt { get; init; }
    public bool IsMissingRuntime { get; init; }
    public string? MissingRuntimeMessage { get; init; }
    public bool IsMissingDependencies { get; init; }
    public string? MissingDependenciesMessage { get; init; }
    public bool AutoStart => Manifest.Supervision?.AutoStart ?? (Manifest.Profile == PluginProfile.Resident);
    public string? EffectiveInstallGuide { get; init; }
    public IReadOnlyList<PluginCustomAction> CustomActions => Manifest.CustomActions;
    public PluginUiConfig? Ui => Manifest.Ui;
    public IReadOnlyList<PluginDependencyConfig> Dependencies { get; init; } = [];
}

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
