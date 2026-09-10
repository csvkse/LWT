using Dapper;
using LinuxWebTool.Infrastructure.Persistence.Entities;

namespace LinuxWebTool.Infrastructure.Persistence;

/// <summary>定时任务仓储。</summary>
[DapperAot]
public partial class ScheduleStore(DbConnectionFactory factory)
{
    public async Task<IEnumerable<ScheduleTask>> GetAllAsync()
    {
        using var db = factory.CreateConnection();
        var sql = "SELECT * FROM schedule_task ORDER BY IsPinned DESC, SortOrder ASC, CreateTime ASC";
        return await db.QueryAsync<ScheduleTask>(sql);
    }

    public async Task<IEnumerable<ScheduleTask>> GetEnabledAsync()
    {
        using var db = factory.CreateConnection();
        var sql = "SELECT * FROM schedule_task WHERE Enabled = 1";
        return await db.QueryAsync<ScheduleTask>(sql);
    }

    public async Task<ScheduleTask?> GetByIdAsync(Guid id)
    {
        using var db = factory.CreateConnection();
        var sql = "SELECT * FROM schedule_task WHERE Id = @Id LIMIT 1";
        return await db.QueryFirstOrDefaultAsync<ScheduleTask>(sql, new { Id = id });
    }

    public async Task InsertAsync(ScheduleTask task)
    {
        task.CreateTime = DateTime.Now;
        task.UpdateTime = DateTime.Now;
        using var db = factory.CreateConnection();
        var sql = @"
INSERT INTO schedule_task (Id, Name, CommandId, CronExpression, Enabled, GroupId, IsPinned, SortOrder, TimeoutSeconds, Arguments, LastRunTime, NextRunTime, CreateTime, UpdateTime) 
VALUES (@Id, @Name, @CommandId, @CronExpression, @Enabled, @GroupId, @IsPinned, @SortOrder, @TimeoutSeconds, @Arguments, @LastRunTime, @NextRunTime, @CreateTime, @UpdateTime)";
        await db.ExecuteAsync(sql, task);
    }

    public async Task UpdateAsync(ScheduleTask task)
    {
        task.UpdateTime = DateTime.Now;
        using var db = factory.CreateConnection();
        var sql = @"
UPDATE schedule_task SET 
    Name = @Name, CommandId = @CommandId, CronExpression = @CronExpression, Enabled = @Enabled, 
    GroupId = @GroupId, IsPinned = @IsPinned, SortOrder = @SortOrder, TimeoutSeconds = @TimeoutSeconds, 
    Arguments = @Arguments, LastRunTime = @LastRunTime, NextRunTime = @NextRunTime, UpdateTime = @UpdateTime
WHERE Id = @Id";
        await db.ExecuteAsync(sql, task);
    }

    public async Task DeleteAsync(Guid id)
    {
        using var db = factory.CreateConnection();
        var sql = "DELETE FROM schedule_task WHERE Id = @Id";
        await db.ExecuteAsync(sql, new { Id = id });
    }

    public async Task<int> CountByGroupAsync(Guid groupId)
    {
        using var db = factory.CreateConnection();
        var sql = "SELECT COUNT(1) FROM schedule_task WHERE GroupId = @GroupId";
        return await db.QueryFirstOrDefaultAsync<int>(sql, new { GroupId = groupId });
    }

    /// <summary>任务执行后回写最近执行时间；nextRun 为空时不改动下次执行时间（手动触发场景）。</summary>
    public async Task UpdateRunInfoAsync(Guid id, DateTime lastRun, DateTime? nextRun)
    {
        using var db = factory.CreateConnection();
        if (nextRun.HasValue)
        {
            var sql = "UPDATE schedule_task SET LastRunTime = @LastRun, NextRunTime = @NextRun WHERE Id = @Id";
            await db.ExecuteAsync(sql, new { Id = id, LastRun = lastRun, NextRun = nextRun.Value });
        }
        else
        {
            var sql = "UPDATE schedule_task SET LastRunTime = @LastRun WHERE Id = @Id";
            await db.ExecuteAsync(sql, new { Id = id, LastRun = lastRun });
        }
    }
}
