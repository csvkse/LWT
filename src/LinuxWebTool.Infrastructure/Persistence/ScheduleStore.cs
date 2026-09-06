using SqlSugar;

namespace LinuxWebTool.Infrastructure.Persistence;

/// <summary>定时任务仓储。</summary>
public class ScheduleStore(ISqlSugarClient db)
{
    public Task<List<ScheduleTask>> GetAllAsync()
    {
        return db.Queryable<ScheduleTask>()
            .OrderBy(t => t.IsPinned, OrderByType.Desc)
            .OrderBy(t => t.SortOrder)
            .OrderBy(t => t.CreateTime)
            .ToListAsync();
    }

    public Task<List<ScheduleTask>> GetEnabledAsync()
    {
        return db.Queryable<ScheduleTask>().Where(t => t.Enabled).ToListAsync();
    }

    public Task<ScheduleTask?> GetByIdAsync(Guid id)
    {
        return db.Queryable<ScheduleTask>().FirstAsync(t => t.Id == id);
    }

    public async Task InsertAsync(ScheduleTask task)
    {
        task.CreateTime = DateTime.Now;
        task.UpdateTime = DateTime.Now;
        await db.Insertable(task).ExecuteCommandAsync();
    }

    public async Task UpdateAsync(ScheduleTask task)
    {
        task.UpdateTime = DateTime.Now;
        await db.Updateable(task).ExecuteCommandAsync();
    }

    public Task DeleteAsync(Guid id)
    {
        return db.Deleteable<ScheduleTask>().Where(t => t.Id == id).ExecuteCommandAsync();
    }

    public Task<int> CountByGroupAsync(Guid groupId)
    {
        return db.Queryable<ScheduleTask>().CountAsync(t => t.GroupId == groupId);
    }

    /// <summary>任务执行后回写最近执行时间；nextRun 为空时不改动下次执行时间（手动触发场景）。</summary>
    public Task UpdateRunInfoAsync(Guid id, DateTime lastRun, DateTime? nextRun)
    {
        var updateable = db.Updateable<ScheduleTask>()
            .SetColumns(t => t.LastRunTime == lastRun)
            .Where(t => t.Id == id);
        return nextRun.HasValue
            ? updateable.SetColumns(t => t.NextRunTime == nextRun.Value).ExecuteCommandAsync()
            : updateable.ExecuteCommandAsync();
    }
}
