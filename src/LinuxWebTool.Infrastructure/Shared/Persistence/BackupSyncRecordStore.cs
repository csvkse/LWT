using Dapper;
using LinuxWebTool.Contracts.Features.BackupSync.Contracts;
using LinuxWebTool.Contracts.Shared.Contracts;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;

namespace LinuxWebTool.Infrastructure.Shared.Persistence;

/// <summary>
/// 备份与同步执行历史记录仓储（DapperAOT 适配）。
/// 提供记录创建、生命周期完成回写、条件分页查询以及基于策略的自动修剪清理。
/// </summary>
[DapperAot]
public partial class BackupSyncRecordStore(DbConnectionFactory factory)
{
    public async Task<Guid> InsertRecordAsync(BackupSyncRecord record)
    {
        using var db = factory.CreateConnection();
        const string sql = @"
INSERT INTO backup_sync_record (
    Id, TaskId, TaskName, Mode, TriggerType, Status, StartTime, EndTime,
    DurationMs, ProcessedFiles, ProcessedBytes, AddedFiles, UpdatedFiles,
    DeletedFiles, SkippedFiles, Summary, ErrorMessage, ClientIp
) VALUES (
    @Id, @TaskId, @TaskName, @Mode, @TriggerType, @Status, @StartTime, @EndTime,
    @DurationMs, @ProcessedFiles, @ProcessedBytes, @AddedFiles, @UpdatedFiles,
    @DeletedFiles, @SkippedFiles, @Summary, @ErrorMessage, @ClientIp
)";
        await db.ExecuteAsync(sql, record);
        return record.Id;
    }

    public async Task UpdateRecordCompletionAsync(
        Guid recordId,
        string status,
        DateTime endTime,
        long durationMs,
        long processedFiles,
        long processedBytes,
        long addedFiles,
        long updatedFiles,
        long deletedFiles,
        long skippedFiles,
        string? summary,
        string? errorMessage)
    {
        using var db = factory.CreateConnection();
        const string sql = @"
UPDATE backup_sync_record SET
    Status = @Status,
    EndTime = @EndTime,
    DurationMs = @DurationMs,
    ProcessedFiles = @ProcessedFiles,
    ProcessedBytes = @ProcessedBytes,
    AddedFiles = @AddedFiles,
    UpdatedFiles = @UpdatedFiles,
    DeletedFiles = @DeletedFiles,
    SkippedFiles = @SkippedFiles,
    Summary = @Summary,
    ErrorMessage = @ErrorMessage
WHERE Id = @Id COLLATE NOCASE";
        await db.ExecuteAsync(sql, new
        {
            Id = recordId,
            Status = status,
            EndTime = endTime,
            DurationMs = durationMs,
            ProcessedFiles = processedFiles,
            ProcessedBytes = processedBytes,
            AddedFiles = addedFiles,
            UpdatedFiles = updatedFiles,
            DeletedFiles = deletedFiles,
            SkippedFiles = skippedFiles,
            Summary = summary,
            ErrorMessage = errorMessage
        });
    }

    public async Task<BackupSyncRecord?> GetRecordByIdAsync(Guid id)
    {
        using var db = factory.CreateConnection();
        const string sql = "SELECT * FROM backup_sync_record WHERE Id = @Id COLLATE NOCASE LIMIT 1";
        return await db.QueryFirstOrDefaultAsync<BackupSyncRecord>(sql, new { Id = id });
    }

    public async Task<PagedResult<BackupSyncRecordItem>> QueryRecordsAsync(BackupSyncRecordQuery query)
    {
        using var db = factory.CreateConnection();
        var page = Math.Max(1, query.Page ?? 1);
        var pageSize = Math.Clamp((query.PageSize ?? 20) <= 0 ? 20 : (query.PageSize ?? 20), 1, 200);

        var parameters = new
        {
            TaskId = query.TaskId,
            Status = string.IsNullOrWhiteSpace(query.Status) ? null : query.Status.Trim(),
        };

        const string whereClause = @"WHERE (@TaskId IS NULL OR TaskId = @TaskId COLLATE NOCASE)
            AND (@Status IS NULL OR Status = @Status COLLATE NOCASE)";

        var total = await db.QueryFirstOrDefaultAsync<int>(
            $"SELECT COUNT(1) FROM backup_sync_record {whereClause}",
            parameters);

        var offset = (page - 1) * pageSize;
        var records = await db.QueryAsync<BackupSyncRecord>(
            $"SELECT * FROM backup_sync_record {whereClause} ORDER BY StartTime DESC LIMIT @PageSize OFFSET @Offset",
            new
            {
                parameters.TaskId,
                parameters.Status,
                PageSize = pageSize,
                Offset = offset
            });

        var items = records.Select(ToItem).ToList();

        return new PagedResult<BackupSyncRecordItem>
        {
            Items = items,
            Total = total,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <summary>
    /// 复合修剪策略：按任务保留最新 N 条记录，并且在此之外超出 D 天的记录彻底删除。
    /// 无论何种情况，最近的失败（Failed / AutoPaused）记录在保留天数内享受豁免，保障故障现场追溯。
    /// </summary>
    public async Task<int> PruneTaskRecordsAsync(Guid taskId, int keepLatestCount, int olderThanDays, CancellationToken ct = default)
    {
        using var db = factory.CreateConnection();
        var safeKeepCount = Math.Max(5, keepLatestCount);
        var cutoff = DateTime.Now.AddDays(-Math.Max(1, olderThanDays));

        // 删除条件：
        // 1. 属于指定任务
        // 2. 创建时间早于截止时间
        // 3. 不属于最新 N 条
        // 4. 不属于最近 3 条未超期失败现场
        const string sql = @"
DELETE FROM backup_sync_record
WHERE TaskId = @TaskId COLLATE NOCASE
  AND StartTime < @Cutoff
  AND Id NOT IN (
      SELECT Id FROM backup_sync_record
      WHERE TaskId = @TaskId COLLATE NOCASE
      ORDER BY StartTime DESC
      LIMIT @KeepCount
  )
  AND Id NOT IN (
      SELECT Id FROM backup_sync_record
      WHERE TaskId = @TaskId COLLATE NOCASE
        AND Status IN ('Failed', 'AutoPaused')
      ORDER BY StartTime DESC
      LIMIT 3
  )";

        return await db.ExecuteAsync(sql, new
        {
            TaskId = taskId,
            Cutoff = cutoff,
            KeepCount = safeKeepCount
        });
    }

    /// <summary>
    /// 手动清空或修剪历史记录
    /// </summary>
    public async Task<int> ClearRecordsAsync(Guid? taskId, int? olderThanDays, CancellationToken ct = default)
    {
        using var db = factory.CreateConnection();
        if (taskId.HasValue && olderThanDays.HasValue)
        {
            var cutoff = DateTime.Now.AddDays(-olderThanDays.Value);
            return await db.ExecuteAsync(
                "DELETE FROM backup_sync_record WHERE TaskId = @TaskId COLLATE NOCASE AND StartTime < @Cutoff",
                new { TaskId = taskId.Value, Cutoff = cutoff });
        }
        if (taskId.HasValue)
        {
            return await db.ExecuteAsync(
                "DELETE FROM backup_sync_record WHERE TaskId = @TaskId COLLATE NOCASE",
                new { TaskId = taskId.Value });
        }
        if (olderThanDays.HasValue)
        {
            var cutoff = DateTime.Now.AddDays(-olderThanDays.Value);
            return await db.ExecuteAsync(
                "DELETE FROM backup_sync_record WHERE StartTime < @Cutoff",
                new { Cutoff = cutoff });
        }

        return await db.ExecuteAsync("DELETE FROM backup_sync_record");
    }

    /// <summary>
    /// 系统引导时自愈：将因系统断电或容器重启遗留为 Running 状态的僵尸记录标记为 Interrupted
    /// </summary>
    public async Task<int> MarkInterruptedRunningRecordsAsync()
    {
        using var db = factory.CreateConnection();
        const string sql = @"
UPDATE backup_sync_record SET
    Status = 'Interrupted',
    EndTime = @Now,
    ErrorMessage = '应用启动检测到上一次服务重启或非正常退出，自动标记为已中断'
WHERE Status = 'Running'";
        return await db.ExecuteAsync(sql, new { Now = DateTime.Now });
    }

    public static BackupSyncRecordItem ToItem(BackupSyncRecord r) => new()
    {
        Id = r.Id,
        TaskId = r.TaskId,
        TaskName = r.TaskName,
        Mode = (TaskMode)r.Mode,
        TriggerType = r.TriggerType,
        Status = r.Status,
        StartTime = r.StartTime,
        EndTime = r.EndTime,
        DurationMs = r.DurationMs,
        ProcessedFiles = r.ProcessedFiles,
        ProcessedBytes = r.ProcessedBytes,
        AddedFiles = r.AddedFiles,
        UpdatedFiles = r.UpdatedFiles,
        DeletedFiles = r.DeletedFiles,
        SkippedFiles = r.SkippedFiles,
        Summary = r.Summary,
        ErrorMessage = r.ErrorMessage,
        ClientIp = r.ClientIp
    };
}
