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
    Native = 5
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
    MissingRuntime = 7
}

public sealed record PluginNetworkConfig
{
    public string Mode { get; init; } = "none"; // "none" | "path" | "port"
    public int? Port { get; init; }
    public string? PortEnv { get; init; }
    public string? PathPrefix { get; init; }
    public bool SupportHttp { get => Mode == "port" || Mode == "path" || Port > 0; init { if (value && Mode == "none") Mode = "port"; } }
}

public sealed record PluginStorageConfig
{
    public bool EnableSqlite { get; init; }
    public bool EnableKv { get; init; }
}

public sealed record PluginSupervisionConfig
{
    public bool AutoRestart { get; init; } = true;
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

public sealed record PluginDependencies
{
    public IReadOnlyList<string> Runners { get; init; } = [];
    public IReadOnlyList<string> Binaries { get; init; } = [];
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
    public IReadOnlyDictionary<string, string>? InstallGuide { get; init; }
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
    public int? ProcessId { get; init; }
    public string? AccessUrl { get; init; }
    public string? GatewayUrl => AccessUrl;
    public string? StatusMessage { get; init; }
    public int CrashCount { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? LastCrashAt { get; init; }
    public bool IsMissingRuntime { get; init; }
    public string? MissingRuntimeMessage { get; init; }
    public string? EffectiveInstallGuide { get; init; }
    public IReadOnlyList<PluginCustomAction> CustomActions => Manifest.CustomActions;
    public PluginUiConfig? Ui => Manifest.Ui;
}
