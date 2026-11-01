namespace LinuxWebTool.Contracts.Features.Plugins.Contracts;

public sealed record PluginCodeDto
{
    public required string FilePath { get; init; }
    public required string Content { get; init; }
    public required IReadOnlyList<string> FileTree { get; init; }
    public string ActiveFile => FilePath;
    public IReadOnlyList<string> Files => FileTree;
    public bool HasBackup { get; init; }
    public DateTimeOffset? BackupTimestamp { get; init; }
}

public sealed record PluginCodeSaveRequest
{
    private string? _filePath;
    public string FilePath { get => _filePath ?? ""; init => _filePath = value; }
    public string File { get => FilePath; init => _filePath = value; }
    public required string Content { get; init; }
    public bool CreateBackup { get; init; } = true;
    public bool RestoreBackup { get; init; }
}

public sealed record PluginDebugRunRequest
{
    public string? Action { get; init; }
    public string? Param { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
}

public sealed record PluginDebugRunResultDto
{
    public bool Success { get; init; }
    public int ExitCode { get; init; }
    public string Output { get; init; } = "";
    public string Error { get; init; } = "";
    public double ElapsedMs { get; init; }
}

public sealed record PluginSourceMetadata
{
    public string? SourceType { get; init; } // "git" | "zip" | "builtin" | "custom"
    public string? RepoUrl { get; init; }
    public string? SubPath { get; init; }
    public string? CommitHash { get; init; }
    public string InstalledVersion { get; init; } = "1.0.0";
    public DateTimeOffset? InstalledAt { get; init; }
}

public sealed record PluginRepoModule
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string Id { get; init; }
    public string Description { get; init; } = "";
    public string Icon { get; init; } = "🧩";
    public string Category { get; init; } = "General";
    public PluginProfile Profile { get; init; } = PluginProfile.Resident;
    public PluginRuntime Runtime { get; init; } = PluginRuntime.Node;
    public bool ExistsLocally { get; init; }
}

public sealed record PluginRepoScanResult
{
    public required string RepoUrl { get; init; }
    public required IReadOnlyList<PluginRepoModule> Modules { get; init; }
}

public sealed record PluginRepoScanRequest
{
    public required string RepoUrl { get; init; }
}

public sealed record PluginRepoApplyRequest
{
    public required string RepoUrl { get; init; }
    public required IReadOnlyList<string> ModulePaths { get; init; }
}

public sealed record PluginBatchImportResult
{
    public int ImportedCount { get; init; }
    public IReadOnlyList<string> ImportedPluginIds { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
}

public sealed record PluginCreateOrUpdateRequest
{
    public required PluginManifest Manifest { get; init; }
    public string? InitialCode { get; init; }
}

public sealed record PluginActionRequest
{
    public required string PluginId { get; init; }
    public required string Action { get; init; }
    public string? Param { get; init; }
}

public sealed record PluginSqliteInfo
{
    public bool Exists { get; init; }
    public long SizeBytes { get; init; }
    public IReadOnlyList<string> TableNames { get; init; } = [];
}

public sealed record PluginKvItem
{
    public required string Key { get; init; }
    public required string Value { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
