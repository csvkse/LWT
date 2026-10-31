namespace LinuxWebTool.Contracts.Features.Workers.Contracts;

/// <summary>
/// 创建/更新 Worker 请求。
/// </summary>
public sealed record WorkerCreateOrUpdateRequest
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public WorkerServiceMode Mode { get; init; } = WorkerServiceMode.Service;
    public WorkerRuntimeType Runtime { get; init; } = WorkerRuntimeType.Python;
    public string Entrypoint { get; init; } = "worker.py";
    public string? ScriptContent { get; init; }
    public bool AutoRestart { get; init; } = true;
    public bool EnableSqlite { get; init; } = true;
    public bool EnableKv { get; init; } = true;
    public Dictionary<string, string>? EnvironmentVariables { get; init; }
}

/// <summary>
/// Git 仓库扫描多服务结果项。
/// </summary>
public sealed record WorkerRepoScanItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public WorkerServiceMode Mode { get; init; }
    public WorkerRuntimeType Runtime { get; init; }
    public string RelativePath { get; init; } = "";
    public bool AlreadyInstalled { get; init; }
}

/// <summary>
/// Git 仓库扫描响应。
/// </summary>
public sealed record WorkerRepoScanResult
{
    public string RepoUrl { get; init; } = "";
    public string TempDirectory { get; init; } = "";
    public IReadOnlyList<WorkerRepoScanItem> Modules { get; init; } = [];
}

/// <summary>
/// 批量导入仓库模块请求。
/// </summary>
public sealed record WorkerRepoApplyRequest
{
    public required string TempDirectory { get; init; }
    public IReadOnlyList<string> ModuleIds { get; init; } = [];
    public bool OverwriteExisting { get; init; } = true;
    public string? RepoUrl { get; init; }
}

/// <summary>
/// 专属 KV 键值对条目。
/// </summary>
public sealed record WorkerKvItem
{
    public required string Key { get; init; }
    public required string Value { get; init; }
    public long? ExpireAtTimestamp { get; init; }
    public string? UpdatedAt { get; init; }
}

/// <summary>
/// 专属 SQLite 概要信息。
/// </summary>
public sealed record WorkerSqliteInfo
{
    public bool Exists { get; init; }
    public long SizeBytes { get; init; }
    public string AbsolutePath { get; init; } = "";
    public IReadOnlyList<string> Tables { get; init; } = [];
}

/// <summary>
/// Worker 通用操作响应 DTO。
/// </summary>
public sealed record WorkerOperationResponse(bool Success, string? Message = null, int Count = 0);

/// <summary>
/// Worker 来源溯源元数据 (source.json)。
/// </summary>
public sealed record WorkerSourceMetadata
{
    public string SourceType { get; init; } = "builtin"; // git, zip, builtin
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
/// Worker 检查更新响应。
/// </summary>
public sealed record WorkerUpdateCheckResponse(
    bool HasUpdate,
    string CurrentVersion,
    string? RemoteVersion,
    string? ReleaseNotes = null);

/// <summary>
/// Worker 在线热更新请求。
/// </summary>
public sealed record WorkerHotUpdateRequest(bool Force = false);

/// <summary>
/// Worker 在线热更新响应。
/// </summary>
public sealed record WorkerHotUpdateResponse(
    bool Success,
    string OldVersion,
    string NewVersion,
    string? Message = null,
    bool RollbackPerformed = false);

/// <summary>
/// Worker 扫描 Git 仓库请求。
/// </summary>
public sealed record WorkerRepoScanRequest
{
    public required string RepoUrl { get; init; }
    public string? TargetType { get; init; } // worker, extension, all
}
