using Dapper;
using LinuxWebTool.Contracts.Models;
using LinuxWebTool.Infrastructure.Persistence.Entities;

namespace LinuxWebTool.Infrastructure.Persistence;

/// <summary>转码任务仓储：分页查询、状态迁移与进度回写。</summary>
[DapperAot]
public partial class TranscodeJobStore(DbConnectionFactory factory)
{
    public async Task<(IEnumerable<TranscodeJob> Items, int Total)> QueryAsync(int page, int pageSize, TranscodeJobStatus? status, Guid? watchRuleId)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        using var db = factory.CreateConnection();
        
        var conditions = new List<string>();
        if (status.HasValue) conditions.Add("Status = @Status");
        if (watchRuleId.HasValue) conditions.Add("WatchRuleId = @WatchRuleId");
        
        var whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";

        var countSql = $"SELECT COUNT(1) FROM transcode_job {whereClause}";
        var dataSql = $@"
            SELECT * FROM transcode_job 
            {whereClause} 
            ORDER BY Status ASC, QueueTime DESC 
            LIMIT @PageSize OFFSET @Offset";
            
        var parameters = new 
        {
            Status = status.HasValue ? (int)status.Value : 0,
            WatchRuleId = watchRuleId.HasValue ? watchRuleId.Value : Guid.Empty,
            PageSize = pageSize,
            Offset = (page - 1) * pageSize
        };

        var total = await db.QueryFirstOrDefaultAsync<int>(countSql, parameters);
        var items = await db.QueryAsync<TranscodeJob>(dataSql, parameters);
        
        return (items, total);
    }

    public async Task<TranscodeJob?> GetByIdAsync(Guid id)
    {
        using var db = factory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<TranscodeJob>(
            "SELECT * FROM transcode_job WHERE Id = @Id", new { Id = id });
    }

    public async Task InsertAsync(TranscodeJob job)
    {
        using var db = factory.CreateConnection();
        job.CreateTime = DateTime.Now;
        job.UpdateTime = DateTime.Now;
        var sql = @"
            INSERT INTO transcode_job (
                Id, SourcePath, OutputPath, PresetId, PresetName, CustomArgs, IsFullCommand, 
                UseHardwareAccel, HardwareBackend, UsedHardwareAccel, CommandLine, FallbackReason, 
                FallbackFromCommand, OutputDir, OutputContainer, OutputMode, Trigger, WatchRuleId, 
                Status, Progress, SpeedText, DurationMs, ErrorOutput, LogFile, SourceSizeBytes, 
                OutputSizeBytes, QueueTime, StartTime, EndTime, CreateTime, UpdateTime
            ) VALUES (
                @Id, @SourcePath, @OutputPath, @PresetId, @PresetName, @CustomArgs, @IsFullCommand, 
                @UseHardwareAccel, @HardwareBackend, @UsedHardwareAccel, @CommandLine, @FallbackReason, 
                @FallbackFromCommand, @OutputDir, @OutputContainer, @OutputMode, @Trigger, @WatchRuleId, 
                @Status, @Progress, @SpeedText, @DurationMs, @ErrorOutput, @LogFile, @SourceSizeBytes, 
                @OutputSizeBytes, @QueueTime, @StartTime, @EndTime, @CreateTime, @UpdateTime
            )";
        await db.ExecuteAsync(sql, job);
    }

    public async Task UpdateAsync(TranscodeJob job)
    {
        using var db = factory.CreateConnection();
        job.UpdateTime = DateTime.Now;
        var sql = @"
            UPDATE transcode_job SET 
                SourcePath = @SourcePath, OutputPath = @OutputPath, PresetId = @PresetId, 
                PresetName = @PresetName, CustomArgs = @CustomArgs, IsFullCommand = @IsFullCommand, 
                UseHardwareAccel = @UseHardwareAccel, HardwareBackend = @HardwareBackend, 
                UsedHardwareAccel = @UsedHardwareAccel, CommandLine = @CommandLine, 
                FallbackReason = @FallbackReason, FallbackFromCommand = @FallbackFromCommand, 
                OutputDir = @OutputDir, OutputContainer = @OutputContainer, OutputMode = @OutputMode, 
                Trigger = @Trigger, WatchRuleId = @WatchRuleId, Status = @Status, 
                Progress = @Progress, SpeedText = @SpeedText, DurationMs = @DurationMs, 
                ErrorOutput = @ErrorOutput, LogFile = @LogFile, SourceSizeBytes = @SourceSizeBytes, 
                OutputSizeBytes = @OutputSizeBytes, QueueTime = @QueueTime, StartTime = @StartTime, 
                EndTime = @EndTime, UpdateTime = @UpdateTime
            WHERE Id = @Id";
        await db.ExecuteAsync(sql, job);
    }

    public async Task DeleteAsync(Guid id)
    {
        using var db = factory.CreateConnection();
        await db.ExecuteAsync("DELETE FROM transcode_job WHERE Id = @Id", new { Id = id });
    }

    /// <summary>删除全部已结束（成功/失败/取消/中断）的任务记录，返回删除数量。</summary>
    public async Task<int> DeleteFinishedAsync()
    {
        using var db = factory.CreateConnection();
        return await db.ExecuteAsync(
            "DELETE FROM transcode_job WHERE Status >= @Status", 
            new { Status = (int)TranscodeJobStatus.Success });
    }

    /// <summary>排队中的任务数（应用启动恢复时重新入队用）。</summary>
    public async Task<IEnumerable<Guid>> GetQueuedIdsAsync()
    {
        using var db = factory.CreateConnection();
        return await db.QueryAsync<Guid>(
            "SELECT Id FROM transcode_job WHERE Status = @Status", 
            new { Status = (int)TranscodeJobStatus.Queued });
    }

    /// <summary>启动恢复：上次运行未结束的""转码中""任务标记为中断。</summary>
    public async Task<int> MarkRunningAsInterruptedAsync()
    {
        using var db = factory.CreateConnection();
        var sql = @"
            UPDATE transcode_job 
            SET Status = @Status, ErrorOutput = @ErrorOutput, EndTime = @EndTime, UpdateTime = @UpdateTime 
            WHERE Status = @RunningStatus";
        return await db.ExecuteAsync(sql, new 
        { 
            Status = (int)TranscodeJobStatus.Interrupted, 
            ErrorOutput = "应用重启，任务被中断（可重试）", 
            EndTime = DateTime.Now, 
            UpdateTime = DateTime.Now,
            RunningStatus = (int)TranscodeJobStatus.Running 
        });
    }

    public async Task<bool> ExistsActiveForPresetAsync(Guid presetId)
    {
        using var db = factory.CreateConnection();
        var count = await db.QueryFirstOrDefaultAsync<int?>(
            "SELECT 1 FROM transcode_job WHERE PresetId = @PresetId AND Status < @SuccessStatus", 
            new { PresetId = presetId, SuccessStatus = (int)TranscodeJobStatus.Success });
        return count.HasValue;
    }

    /// <summary>排队 / 运行中任务的源路径与输出路径全集（监听扫描排除在途文件用）。</summary>
    public async Task<HashSet<string>> GetActivePathsAsync()
    {
        using var db = factory.CreateConnection();
        var paths = await db.QueryAsync<string>(
            "SELECT SourcePath FROM transcode_job WHERE Status < @SuccessStatus AND SourcePath IS NOT NULL " +
            "UNION " +
            "SELECT OutputPath FROM transcode_job WHERE Status < @SuccessStatus AND OutputPath IS NOT NULL",
            new { SuccessStatus = (int)TranscodeJobStatus.Success });
            
        return paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
