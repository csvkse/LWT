namespace LinuxWebTool.Contracts.Features.BackupSync.Contracts;

/// <summary>
/// 备份与同步任务执行生命周期阶段（状态机枚举）
/// </summary>
public enum BackupExecutionPhase
{
    Idle = 0,               // 空闲就绪
    PreflightChecking = 1,  // 正在执行挂载状态、源目录可达性与安全预检
    Scanning = 2,           // 正在扫描源端目录结构并匹配排除规则
    Transferring = 3,       // 正在进行差异比对、数据写入与硬链建立
    CleaningTarget = 4,     // 正在清理目标端多余文件或移入回收站（同步模式）
    RetentionPruning = 5,   // 正在执行 GFS 快照轮转修剪与过期清理
    Completed = 6,          // 任务圆满完成
    Faulted = 7,            // 任务执行失败
    AutoPaused = 8          // 连续失败触发断路器自动熔断暂停
}

/// <summary>
/// 备份与同步任务执行进度不可变实时快照（供前端轮询与仪表盘呈现）
/// </summary>
public sealed record BackupProgressSnapshot
{
    public Guid TaskId { get; init; }
    public string TaskName { get; init; } = string.Empty;
    public int Mode { get; init; } // 1 = Sync, 2 = Backup
    public BackupExecutionPhase Phase { get; init; } = BackupExecutionPhase.Idle;
    public string PhaseDescription { get; init; } = string.Empty;
    public bool IsRunning { get; init; }
    public DateTime? StartTime { get; init; }
    public long ElapsedMs { get; init; }

    // 文件维度统计
    public long ScannedFiles { get; init; }
    public long ProcessedFiles { get; init; }
    public long SkippedFiles { get; init; }
    public double FilePercent { get; init; } // 0.0 ~ 100.0%

    // 容量维度统计
    public long ScannedBytes { get; init; }
    public long ProcessedBytes { get; init; }
    public double BytePercent { get; init; } // 0.0 ~ 100.0%

    // 吞吐速率与时间预估
    public double SpeedBytesPerSec { get; init; } // 瞬时传输速率 (Byte/s)
    public double SpeedFilesPerSec { get; init; } // 瞬时文件处理速率 (Files/s)
    public long? EstimatedRemainingMs { get; init; } // 预估剩余毫秒数 (ETA)

    // 正在处理的实时路径追踪
    public string? CurrentItemPath { get; init; }
}
