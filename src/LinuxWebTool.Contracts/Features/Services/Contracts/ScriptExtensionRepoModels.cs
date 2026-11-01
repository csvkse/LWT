namespace LinuxWebTool.Contracts.Features.Services.Contracts;

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
#endregion
