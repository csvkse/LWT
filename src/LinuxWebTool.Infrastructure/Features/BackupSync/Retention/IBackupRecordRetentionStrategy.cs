using LinuxWebTool.Infrastructure.Shared.Persistence;

namespace LinuxWebTool.Infrastructure.Features.BackupSync.Retention;

/// <summary>
/// 备份与同步执行记录自动清理保留策略接口（策略模式）。
/// 规范对历史执行记录的修剪与生命周期管理行为。
/// </summary>
public interface IBackupRecordRetentionStrategy
{
    Task PruneRecordsAsync(Guid taskId, BackupSyncRecordStore store, CancellationToken ct = default);
}

/// <summary>
/// 智能复合保留策略（Smart Hybrid Retention Strategy）：
/// 1. 数量保底：为每个任务保底保留最新 N 条记录（默认 100 条）；
/// 2. 时间淘汰：超出 N 条的记录中，超过 D 天（默认 30 天）的记录自动物理修剪；
/// 3. 故障保护：最近的失败/熔断记录在保留周期内享受保护，防止被密集成功的调度冲刷掩盖。
/// </summary>
public class SmartHybridRecordRetentionStrategy : IBackupRecordRetentionStrategy
{
    public int KeepLatestCount { get; set; } = 100;
    public int RetentionDays { get; set; } = 30;

    public async Task PruneRecordsAsync(Guid taskId, BackupSyncRecordStore store, CancellationToken ct = default)
    {
        await store.PruneTaskRecordsAsync(taskId, KeepLatestCount, RetentionDays, ct);
    }
}
