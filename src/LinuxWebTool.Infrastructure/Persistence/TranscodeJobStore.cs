using LinuxWebTool.Contracts.Models;
using LinuxWebTool.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace LinuxWebTool.Infrastructure.Persistence;

/// <summary>转码任务仓储：分页查询、状态迁移与进度回写。</summary>
public class TranscodeJobStore(ISqlSugarClient db)
{
    public async Task<(List<TranscodeJob> Items, int Total)> QueryAsync(int page, int pageSize, TranscodeJobStatus? status, Guid? watchRuleId)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.Queryable<TranscodeJob>();
        if (status.HasValue)
        {
            query = query.Where(j => j.Status == (int)status.Value);
        }
        if (watchRuleId.HasValue)
        {
            query = query.Where(j => j.WatchRuleId == watchRuleId.Value);
        }

        var total = await query.CountAsync();
        var items = await query
            .OrderBy(j => j.Status, OrderByType.Asc) // 排队 / 运行中的排前面（状态值小）
            .OrderBy(j => j.QueueTime, OrderByType.Desc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
        return (items, total);
    }

    public async Task<TranscodeJob?> GetByIdAsync(Guid id)
    {
        return (TranscodeJob?)await db.Queryable<TranscodeJob>().FirstAsync(j => j.Id == id);
    }

    public async Task InsertAsync(TranscodeJob job)
    {
        job.CreateTime = DateTime.Now;
        job.UpdateTime = DateTime.Now;
        await db.Insertable(job).ExecuteCommandAsync();
    }

    public Task UpdateAsync(TranscodeJob job)
    {
        job.UpdateTime = DateTime.Now;
        return db.Updateable(job).ExecuteCommandAsync();
    }

    public Task DeleteAsync(Guid id)
    {
        return db.Deleteable<TranscodeJob>().Where(j => j.Id == id).ExecuteCommandAsync();
    }

    /// <summary>删除全部已结束（成功/失败/取消/中断）的任务记录，返回删除数量。</summary>
    public async Task<int> DeleteFinishedAsync()
    {
        return await db.Deleteable<TranscodeJob>()
            .Where(j => j.Status >= (int)TranscodeJobStatus.Success)
            .ExecuteCommandAsync();
    }

    /// <summary>排队中的任务数（应用启动恢复时重新入队用）。</summary>
    public Task<List<Guid>> GetQueuedIdsAsync()
    {
        return db.Queryable<TranscodeJob>()
            .Where(j => j.Status == (int)TranscodeJobStatus.Queued)
            .Select(j => j.Id)
            .ToListAsync();
    }

    /// <summary>启动恢复：上次运行未结束的"转码中"任务标记为中断。</summary>
    public Task<int> MarkRunningAsInterruptedAsync()
    {
        return db.Updateable<TranscodeJob>()
            .SetColumns(j => new TranscodeJob
            {
                Status = (int)TranscodeJobStatus.Interrupted,
                ErrorOutput = "应用重启，任务被中断（可重试）",
                EndTime = DateTime.Now,
                UpdateTime = DateTime.Now,
            })
            .Where(j => j.Status == (int)TranscodeJobStatus.Running)
            .ExecuteCommandAsync();
    }

    public Task<bool> ExistsActiveForPresetAsync(Guid presetId)
    {
        return db.Queryable<TranscodeJob>()
            .AnyAsync(j => j.PresetId == presetId && j.Status < (int)TranscodeJobStatus.Success);
    }

    /// <summary>排队 / 运行中任务的源路径与输出路径全集（监听扫描排除在途文件用）。</summary>
    public async Task<HashSet<string>> GetActivePathsAsync()
    {
        var jobs = await db.Queryable<TranscodeJob>()
            .Where(j => j.Status < (int)TranscodeJobStatus.Success)
            .Select(j => new TranscodeJob { SourcePath = j.SourcePath, OutputPath = j.OutputPath })
            .ToListAsync();
        return jobs
            .SelectMany(j => new[] { j.SourcePath, j.OutputPath })
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
