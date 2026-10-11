namespace LinuxWebTool.Contracts.Features.BackupSync.Contracts;

public enum TaskMode
{
    Sync = 1,     // 直接同步
    Backup = 2    // 增量版本备份
}

public enum SyncDirection
{
    OneWayMirror = 1,   // 单向镜像（目标跟随源端）
    OneWayAddOnly = 2   // 单向累加（源端删除时不删目标）
}

public enum DeleteHandling
{
    PermanentDelete = 1, // 立即永久删除
    MoveToTrash = 2      // 暂存到目标回收站
}

/// <summary>备份与同步任务 DTO</summary>
public sealed record BackupSyncTaskItem
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public TaskMode Mode { get; init; } = TaskMode.Backup;
    public string SourcePath { get; init; } = string.Empty;
    public string TargetPath { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;

    // 调度配置（Quartz Cron）
    public string CronExpression { get; init; } = "0 0 2 * * ?";
    public string CronDescription { get; init; } = "每天 02:00";

    // 同步专属配置 (Mode == Sync)
    public SyncDirection SyncDirection { get; init; } = SyncDirection.OneWayMirror;
    public DeleteHandling DeleteHandling { get; init; } = DeleteHandling.MoveToTrash;
    public int TrashRetentionDays { get; init; } = 30;

    // 增量备份专属配置 (Mode == Backup)
    public int KeepLastCount { get; init; } = 5;      // 兜底保留最近 N 次
    public int KeepDays { get; init; } = 7;           // 保留最近 D 天
    public int KeepWeeks { get; init; } = 4;          // 保留最近 W 周
    public int KeepMonths { get; init; } = 12;        // 保留最近 M 月

    // 过滤排除规则
    public List<string> ExcludePatterns { get; init; } = [];

    // 运行状态跟踪
    public DateTime? LastRunTime { get; init; }
    public string? LastRunStatus { get; init; }
    public string? LastRunMessage { get; init; }
    public DateTime? NextRunTime { get; init; }
    public DateTime CreateTime { get; init; }
    public DateTime UpdateTime { get; init; }
}

/// <summary>创建或更新备份/同步任务请求</summary>
public sealed record SaveBackupSyncTaskRequest
{
    public string Name { get; init; } = string.Empty;
    public TaskMode Mode { get; init; } = TaskMode.Backup;
    public string SourcePath { get; init; } = string.Empty;
    public string TargetPath { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    public string CronExpression { get; init; } = "0 0 2 * * ?";
    public SyncDirection SyncDirection { get; init; } = SyncDirection.OneWayMirror;
    public DeleteHandling DeleteHandling { get; init; } = DeleteHandling.MoveToTrash;
    public int TrashRetentionDays { get; init; } = 30;
    public int KeepLastCount { get; init; } = 5;
    public int KeepDays { get; init; } = 7;
    public int KeepWeeks { get; init; } = 4;
    public int KeepMonths { get; init; } = 12;
    public List<string>? ExcludePatterns { get; init; }
}

/// <summary>快照元数据记录</summary>
public sealed record BackupSnapshotItem
{
    public string SnapshotId { get; init; } = string.Empty; // 目录名 如 20261011_020000
    public Guid TaskId { get; init; }
    public DateTime CreatedAt { get; init; }
    public long TotalFiles { get; init; }
    public long TotalSizeBytes { get; init; }
    public string SnapshotPath { get; init; } = string.Empty;
    public string Tag { get; init; } = string.Empty; // Daily / Weekly / Monthly / Manual / Latest
    public bool IsPinned { get; init; } = false;    // 是否已锁定保护（防止批量清理误删）
    public string? Note { get; init; }              // 用户自定义快照备注
}

/// <summary>快照目录树节点（用于快照内容在线浏览）</summary>
public sealed record SnapshotFileNode
{
    public string Name { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public long SizeBytes { get; init; }
    public DateTime LastModified { get; init; }
    public List<SnapshotFileNode>? Children { get; init; }
}

/// <summary>批量删除快照请求</summary>
public sealed record BatchDeleteSnapshotsRequest
{
    public Guid TaskId { get; init; }
    public List<string> SnapshotIds { get; init; } = []; // 手动勾选的 SnapshotId 列表
    public DateTime? OlderThan { get; init; }             // 可选：早于此时间的快照
    public int? KeepLatestCount { get; init; }            // 可选：仅保留最新 N 个
    public bool SkipPinned { get; init; } = true;         // 是否自动跳过已锁定快照（默认强安全保护）
}

/// <summary>批量删除快照执行与演练结果</summary>
public sealed record BatchDeleteSnapshotsResult
{
    public int TotalTargeted { get; init; }               // 命中的目标快照总数
    public int DeletedCount { get; init; }                // 实际删除/预演删除的快照数
    public int SkippedPinnedCount { get; init; }          // 因锁定而被保护跳过的快照数
    public long EstimatedReclaimedBytes { get; init; }    // 预估释放的磁盘空间字节数
    public List<string> AffectedSnapshotIds { get; init; } = [];
}

/// <summary>锁定/解锁快照请求</summary>
public sealed record TogglePinSnapshotRequest
{
    public bool Pinned { get; init; }
    public string? Note { get; init; }
}

/// <summary>还原快照请求</summary>
public sealed record RestoreSnapshotRequest
{
    public string? TargetRestorePath { get; init; }       // 为空则还原覆盖到源目录
    public bool CleanTargetBeforeRestore { get; init; }   // 还原前是否清空目标目录
}

/// <summary>还原快照结果</summary>
public sealed record RestoreSnapshotResult
{
    public int RestoredFilesCount { get; init; }
    public long RestoredBytes { get; init; }
    public string TargetDirectory { get; init; } = string.Empty;
    public long DurationMs { get; init; }
}

/// <summary>保留修剪预演结果</summary>
public sealed record PrunePreviewResult
{
    public List<BackupSnapshotItem> KeptSnapshots { get; init; } = [];
    public List<BackupSnapshotItem> PrunedSnapshots { get; init; } = [];
    public long ReclaimableSizeBytes { get; init; }
}

/// <summary>Cron 预演计算请求</summary>
public sealed record CronPreviewRequest
{
    public string CronExpression { get; init; } = string.Empty;
    public int Count { get; init; } = 5;
}

/// <summary>Cron 预演计算响应</summary>
public sealed record CronPreviewResponse
{
    public string NormalizedCron { get; init; } = string.Empty;
    public List<DateTime> NextRunTimes { get; init; } = [];
    public bool IsValid { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>任务即时运行结果响应</summary>
public sealed record TaskRunResultResponse
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public long DurationMs { get; init; }
    public long ProcessedFiles { get; init; }
    public long ProcessedBytes { get; init; }
}
