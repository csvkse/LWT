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
    public bool IsEnabled { get; init; } = true;
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
    public string Target { get; init; } = "container";
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
    public bool IsEnabled { get; init; } = true;
    public ExtensionSourceMetadata? Source { get; init; }
    public string Target { get; init; } = "container";
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
    public string? Target { get; init; }
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
    public string? TargetType { get; init; } // extension, worker, all
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

/// <summary>
/// 扩展来源溯源元数据 (source.json)。
/// </summary>
public sealed record ExtensionSourceMetadata
{
    public string SourceType { get; init; } = "builtin";
    public string? RepoUrl { get; init; }
    public string? SubDirectory { get; init; }
    public string InstalledVersion { get; init; } = "1.0.0";
    public string? InstalledCommitHash { get; init; }
    public DateTime InstalledAt { get; init; } = DateTime.UtcNow;
    public DateTime? LastCheckedAt { get; init; }
    public string? RemoteVersion { get; init; }
    public bool HasUpdate { get; init; }
}

/// <summary>
/// 扩展检查更新响应。
/// </summary>
public sealed record ExtensionUpdateCheckResponse(
    bool HasUpdate,
    string CurrentVersion,
    string? RemoteVersion,
    string? ReleaseNotes = null);

/// <summary>
/// 扩展在线热更新请求。
/// </summary>
public sealed record ExtensionHotUpdateRequest(bool Force = false);

/// <summary>
/// 扩展在线热更新响应。
/// </summary>
public sealed record ExtensionHotUpdateResponse(
    bool Success,
    string OldVersion,
    string NewVersion,
    string? Message = null,
    bool RollbackPerformed = false);

/// <summary>
/// 扩展服务代码与文件结构 DTO。
/// </summary>
public sealed record ExtensionCodeDto
{
    public required string ExtensionId { get; init; }
    public required string SelectedFile { get; init; }
    public required string Content { get; init; }
    public required IReadOnlyList<string> AvailableFiles { get; init; }
    public required IReadOnlyList<string> Backups { get; init; }
}

/// <summary>
/// 扩展服务代码保存请求。
/// </summary>
public sealed record ExtensionCodeSaveRequest
{
    public string? FilePath { get; init; }
    public required string Content { get; init; }
}

/// <summary>
/// 扩展服务调试运行请求。
/// </summary>
public sealed record ExtensionDebugRunRequest
{
    public string? HookName { get; init; } = "status";
    public Dictionary<string, string>? Params { get; init; }
    public int TimeoutSeconds { get; init; } = 8;
    public string? Target { get; init; }
}

/// <summary>
/// 扩展服务快速创建请求。
/// </summary>
public sealed record ExtensionCreateRequest
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string Runner { get; init; } = "bash"; // bash, pwsh, python3, node
    public string? StartScript { get; init; }
    public string? StatusScript { get; init; }
    public string Target { get; init; } = "container";
}

/// <summary>
/// 调试执行结果通用 DTO。
/// </summary>
public sealed record ExtensionDebugRunResultDto
{
    public required bool Success { get; init; }
    public required int ExitCode { get; init; }
    public required string StandardOutput { get; init; }
    public required string StandardError { get; init; }
    public required long ElapsedMilliseconds { get; init; }
}
#endregion
