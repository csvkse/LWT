namespace LinuxWebTool.Contracts.Features.Services.Contracts;

/// <summary>
/// 扩展配置表单项定义。
/// </summary>
public sealed record ExtensionFormField
{
    public required string Field { get; init; }
    public required string Label { get; init; }
    public string Type { get; init; } = "text"; // text, number, boolean, password, select
    public string? DefaultValue { get; init; }
    public bool Required { get; init; }
    public IReadOnlyList<string> Options { get; init; } = [];
}

/// <summary>
/// 扩展插件能力清单。
/// </summary>
public sealed record ExtensionCapabilities
{
    public bool Lifecycle { get; init; } = true;
    public bool Config { get; init; } = false;
    public bool Logs { get; init; } = true;
}

/// <summary>
/// 动态脚本扩展清单元数据 (manifest.json)。
/// </summary>
public sealed record ExtensionManifest
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Category { get; init; } = "custom";
    public string Version { get; init; } = "1.0.0";
    public string? Author { get; init; }
    public string? Description { get; init; }
    public string? Icon { get; init; }
    public IReadOnlyList<string> SupportedPlatforms { get; init; } = ["linux", "windows"];
    public ExtensionCapabilities Capabilities { get; init; } = new();
    public IReadOnlyList<ExtensionFormField> ConfigForm { get; init; } = [];
}

/// <summary>
/// 扩展插件运行态实例。
/// </summary>
public sealed record ExtensionInstance
{
    public required ExtensionManifest Manifest { get; init; }
    public required string Status { get; init; } // Running, Stopped, Degraded, Unknown
    public string? StatusMessage { get; init; }
    public int? Pid { get; init; }
    public Dictionary<string, string> CurrentConfig { get; init; } = [];
}

/// <summary>
/// 动态扩展操作请求。
/// </summary>
public sealed record ExtensionActionRequest
{
    public required string ExtensionId { get; init; }
    public required string Action { get; init; } // start, stop, restart, config_set
    public Dictionary<string, string>? ConfigValues { get; init; }
}
