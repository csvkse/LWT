namespace LinuxWebTool.Infrastructure.Shared.Persistence.Entities;

public class BackupSyncTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public int Mode { get; set; } // 1 = Sync, 2 = Backup
    public string SourcePath { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    // 调度
    public string CronExpression { get; set; } = "0 0 2 * * ?";
    public string? CronDescription { get; set; }

    // 同步专属
    public int SyncDirection { get; set; } = 1; // 1 = OneWayMirror, 2 = OneWayAddOnly
    public int DeleteHandling { get; set; } = 2; // 1 = PermanentDelete, 2 = MoveToTrash
    public int TrashRetentionDays { get; set; } = 30;

    // 增量备份专属
    public int KeepLastCount { get; set; } = 5;
    public int KeepDays { get; set; } = 7;
    public int KeepWeeks { get; set; } = 4;
    public int KeepMonths { get; set; } = 12;

    // 过滤排除规则（以分号或逗号分隔的通配符）
    public string? ExcludePatterns { get; set; }

    // 运行状态
    public DateTime? LastRunTime { get; set; }
    public string? LastRunStatus { get; set; }
    public string? LastRunMessage { get; set; }
    public DateTime? NextRunTime { get; set; }

    public int ConsecutiveFailures { get; set; } = 0;
    public string? AutoPausedReason { get; set; }

    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

public class BackupSnapshot
{
    public string SnapshotId { get; set; } = string.Empty;
    public Guid TaskId { get; set; }
    public DateTime CreatedAt { get; set; }
    public long TotalFiles { get; set; }
    public long TotalSizeBytes { get; set; }
    public string SnapshotPath { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public bool IsPinned { get; set; }
    public string? Note { get; set; }
}

public class BackupSyncRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TaskId { get; set; }
    public string TaskName { get; set; } = string.Empty;
    public int Mode { get; set; } // 1 = Sync, 2 = Backup
    public int TriggerType { get; set; } = 1; // 1 = Manual, 2 = Scheduled
    public string Status { get; set; } = "Running"; // Running, Success, Failed, AutoPaused, Interrupted
    public DateTime StartTime { get; set; } = DateTime.Now;
    public DateTime? EndTime { get; set; }
    public long DurationMs { get; set; }
    public long ProcessedFiles { get; set; }
    public long ProcessedBytes { get; set; }
    public long AddedFiles { get; set; }
    public long UpdatedFiles { get; set; }
    public long DeletedFiles { get; set; }
    public long SkippedFiles { get; set; }
    public string? Summary { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ClientIp { get; set; }
}
