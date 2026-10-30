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
/// 扩展声明的环境与工具依赖。
/// </summary>
public sealed record ExtensionDependencies
{
    /// <summary>
    /// 所需的解释器运行时（如 sh, bash, node, python3, pwsh, powershell）。
    /// </summary>
    public IReadOnlyList<string> Runners { get; init; } = [];

    /// <summary>
    /// 所需的底层命令行可执行程序（如 caddy, last, lastb, who, ufw）。
    /// </summary>
    public IReadOnlyList<string> Binaries { get; init; } = [];
}

/// <summary>
/// 扩展自定义操作动作定义（如 records, banned_list, ban_ip 等）。
/// </summary>
public sealed record ExtensionCustomAction
{
    public required string Action { get; init; }
    public required string Label { get; init; }
    public string? Icon { get; init; }
    public string? Description { get; init; }
    public bool RequireParam { get; init; }
    public string? ParamName { get; init; }
    public string? ParamPlaceholder { get; init; }
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
    public ExtensionDependencies Dependencies { get; init; } = new();
    public Dictionary<string, string> InstallGuide { get; init; } = [];
    public IReadOnlyList<ExtensionCustomAction> CustomActions { get; init; } = [];
    public IReadOnlyList<ExtensionFormField> ConfigForm { get; init; } = [];
}

/// <summary>
/// 扩展插件运行态实例。
/// </summary>
public sealed record ExtensionInstance
{
    public required ExtensionManifest Manifest { get; init; }
    public required string Status { get; init; } // Running, Stopped, Degraded, MissingRuntime, MissingBinary, Unknown
    public string? StatusMessage { get; init; }
    public int? Pid { get; init; }
    public Dictionary<string, string> CurrentConfig { get; init; } = [];
    public IReadOnlyList<string> MissingRunners { get; init; } = [];
    public IReadOnlyList<string> MissingBinaries { get; init; } = [];
    public string? InstallGuideText { get; init; }
}

/// <summary>
/// 动态扩展操作请求。
/// </summary>
public sealed record ExtensionActionRequest
{
    public required string ExtensionId { get; init; }
    public required string Action { get; init; } // start, stop, restart, config_set 或自定义 action
    public Dictionary<string, string>? ConfigValues { get; init; }
    public Dictionary<string, string>? ActionParams { get; init; }
}

#region Mono-repo & GitHub 仓库导入模型
/// <summary>
/// 仓库扫描探针发现的扩展模块项。
/// </summary>
public sealed record DiscoveredModuleItem
{
    public required string RelativePath { get; init; }
    public required ExtensionManifest Manifest { get; init; }
    public required bool IsCompatibleWithCurrentOs { get; init; }
    public required bool HasMissingRunners { get; init; }
    public required IReadOnlyList<string> MissingRunners { get; init; }
    public required bool AlreadyInstalled { get; init; }
}

/// <summary>
/// 仓库探测请求。
/// </summary>
public sealed record InspectRepoModulesRequest
{
    public required string RepositoryUrl { get; init; }
    public string? ProxyPrefix { get; init; }
    public string? Branch { get; init; }
}

/// <summary>
/// 仓库探测结果响应。
/// </summary>
public sealed record InspectRepoModulesResponse
{
    public required string RepositoryUrl { get; init; }
    public required string Branch { get; init; }
    public string? CommitHash { get; init; }
    public required IReadOnlyList<DiscoveredModuleItem> Modules { get; init; }
}

/// <summary>
/// 批量导入扩展请求。
/// </summary>
public sealed record BatchImportModulesRequest
{
    public required string RepositoryUrl { get; init; }
    public string? ProxyPrefix { get; init; }
    public string? Branch { get; init; }
    public required IReadOnlyList<string> SelectedModulePaths { get; init; }
    public bool OverwriteExisting { get; init; } = true;
}

/// <summary>
/// 批量导入扩展结果。
/// </summary>
public sealed record BatchImportModulesResult
{
    public bool Success { get; init; }
    public string? Message { get; init; }
    public IReadOnlyList<string> ImportedExtensionIds { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
}
#endregion
