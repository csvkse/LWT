using Dapper;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;

namespace LinuxWebTool.Infrastructure.Shared.Persistence;

/// <summary>备份与同步任务及快照元数据仓储。</summary>
[DapperAot]
public partial class BackupSyncStore(DbConnectionFactory factory)
{
    public async Task<IEnumerable<BackupSyncTask>> GetAllTasksAsync()
    {
        using var db = factory.CreateConnection();
        const string sql = "SELECT * FROM backup_sync_task ORDER BY UpdateTime DESC";
        return await db.QueryAsync<BackupSyncTask>(sql);
    }

    public async Task<IEnumerable<BackupSyncTask>> GetEnabledTasksAsync()
    {
        using var db = factory.CreateConnection();
        const string sql = "SELECT * FROM backup_sync_task WHERE Enabled = 1";
        return await db.QueryAsync<BackupSyncTask>(sql);
    }

    public async Task<BackupSyncTask?> GetTaskByIdAsync(Guid id)
    {
        using var db = factory.CreateConnection();
        const string sql = "SELECT * FROM backup_sync_task WHERE Id = @Id COLLATE NOCASE LIMIT 1";
        return await db.QueryFirstOrDefaultAsync<BackupSyncTask>(sql, new { Id = id });
    }

    public async Task InsertTaskAsync(BackupSyncTask task)
    {
        task.CreateTime = DateTime.Now;
        task.UpdateTime = DateTime.Now;
        using var db = factory.CreateConnection();
        const string sql = @"
INSERT INTO backup_sync_task (
    Id, Name, Mode, SourcePath, TargetPath, Enabled, CronExpression, CronDescription,
    SyncDirection, DeleteHandling, TrashRetentionDays,
    KeepLastCount, KeepDays, KeepWeeks, KeepMonths, ExcludePatterns,
    LastRunTime, LastRunStatus, LastRunMessage, NextRunTime, CreateTime, UpdateTime
) VALUES (
    @Id, @Name, @Mode, @SourcePath, @TargetPath, @Enabled, @CronExpression, @CronDescription,
    @SyncDirection, @DeleteHandling, @TrashRetentionDays,
    @KeepLastCount, @KeepDays, @KeepWeeks, @KeepMonths, @ExcludePatterns,
    @LastRunTime, @LastRunStatus, @LastRunMessage, @NextRunTime, @CreateTime, @UpdateTime
)";
        await db.ExecuteAsync(sql, task);
    }

    public async Task UpdateTaskAsync(BackupSyncTask task)
    {
        task.UpdateTime = DateTime.Now;
        using var db = factory.CreateConnection();
        const string sql = @"
UPDATE backup_sync_task SET
    Name = @Name,
    Mode = @Mode,
    SourcePath = @SourcePath,
    TargetPath = @TargetPath,
    Enabled = @Enabled,
    CronExpression = @CronExpression,
    CronDescription = @CronDescription,
    SyncDirection = @SyncDirection,
    DeleteHandling = @DeleteHandling,
    TrashRetentionDays = @TrashRetentionDays,
    KeepLastCount = @KeepLastCount,
    KeepDays = @KeepDays,
    KeepWeeks = @KeepWeeks,
    KeepMonths = @KeepMonths,
    ExcludePatterns = @ExcludePatterns,
    UpdateTime = @UpdateTime
WHERE Id = @Id COLLATE NOCASE";
        await db.ExecuteAsync(sql, task);
    }

    public async Task UpdateTaskRunInfoAsync(Guid id, string? status, string? message, DateTime? lastRun, DateTime? nextRun)
    {
        using var db = factory.CreateConnection();
        const string sql = @"
UPDATE backup_sync_task SET
    LastRunStatus = @Status,
    LastRunMessage = @Message,
    LastRunTime = @LastRun,
    NextRunTime = @NextRun,
    UpdateTime = @UpdateTime
WHERE Id = @Id COLLATE NOCASE";
        await db.ExecuteAsync(sql, new { Id = id, Status = status, Message = message, LastRun = lastRun, NextRun = nextRun, UpdateTime = DateTime.Now });
    }

    public async Task DeleteTaskAsync(Guid id)
    {
        using var db = factory.CreateConnection();
        const string sqlTask = "DELETE FROM backup_sync_task WHERE Id = @Id COLLATE NOCASE";
        const string sqlSnapshots = "DELETE FROM backup_snapshot WHERE TaskId = @Id COLLATE NOCASE";
        await db.ExecuteAsync(sqlSnapshots, new { Id = id });
        await db.ExecuteAsync(sqlTask, new { Id = id });
    }

    public async Task<IEnumerable<BackupSnapshot>> GetSnapshotsByTaskIdAsync(Guid taskId)
    {
        using var db = factory.CreateConnection();
        const string sql = "SELECT * FROM backup_snapshot WHERE TaskId = @TaskId COLLATE NOCASE ORDER BY CreatedAt DESC";
        return await db.QueryAsync<BackupSnapshot>(sql, new { TaskId = taskId });
    }

    public async Task<BackupSnapshot?> GetSnapshotAsync(Guid taskId, string snapshotId)
    {
        using var db = factory.CreateConnection();
        const string sql = "SELECT * FROM backup_snapshot WHERE TaskId = @TaskId COLLATE NOCASE AND SnapshotId = @SnapshotId COLLATE NOCASE LIMIT 1";
        return await db.QueryFirstOrDefaultAsync<BackupSnapshot>(sql, new { TaskId = taskId, SnapshotId = snapshotId });
    }

    public async Task InsertSnapshotAsync(BackupSnapshot snapshot)
    {
        using var db = factory.CreateConnection();
        const string sql = @"
INSERT OR REPLACE INTO backup_snapshot (
    SnapshotId, TaskId, CreatedAt, TotalFiles, TotalSizeBytes, SnapshotPath, Tag, IsPinned, Note
) VALUES (
    @SnapshotId, @TaskId, @CreatedAt, @TotalFiles, @TotalSizeBytes, @SnapshotPath, @Tag, @IsPinned, @Note
)";
        await db.ExecuteAsync(sql, snapshot);
    }

    public async Task DeleteSnapshotAsync(Guid taskId, string snapshotId)
    {
        using var db = factory.CreateConnection();
        const string sql = "DELETE FROM backup_snapshot WHERE TaskId = @TaskId COLLATE NOCASE AND SnapshotId = @SnapshotId COLLATE NOCASE";
        await db.ExecuteAsync(sql, new { TaskId = taskId, SnapshotId = snapshotId });
    }

    public async Task BatchDeleteSnapshotsAsync(Guid taskId, IEnumerable<string> snapshotIds)
    {
        using var db = factory.CreateConnection();
        const string sql = "DELETE FROM backup_snapshot WHERE TaskId = @TaskId COLLATE NOCASE AND SnapshotId = @SnapshotId COLLATE NOCASE";
        var items = snapshotIds.Select(sid => new { TaskId = taskId, SnapshotId = sid });
        await db.ExecuteAsync(sql, items);
    }

    public async Task TogglePinSnapshotAsync(Guid taskId, string snapshotId, bool isPinned, string? note)
    {
        using var db = factory.CreateConnection();
        const string sql = @"
UPDATE backup_snapshot SET
    IsPinned = @IsPinned,
    Note = COALESCE(@Note, Note)
WHERE TaskId = @TaskId COLLATE NOCASE AND SnapshotId = @SnapshotId COLLATE NOCASE";
        await db.ExecuteAsync(sql, new { TaskId = taskId, SnapshotId = snapshotId, IsPinned = isPinned, Note = note });
    }
}
