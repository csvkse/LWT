using SqlSugar;

namespace LinuxWebTool.Infrastructure.Persistence;

/// <summary>执行历史（调用历史 / 定时任务执行日志）仓储。</summary>
public class ExecutionStore(ISqlSugarClient db)
{
    public async Task<Guid> InsertAsync(ExecutionRecord record)
    {
        record.Id = Guid.NewGuid();
        await db.Insertable(record).ExecuteCommandAsync();
        return record.Id;
    }

    public Task<ExecutionRecord?> GetByIdAsync(Guid id)
    {
        return db.Queryable<ExecutionRecord>().FirstAsync(r => r.Id == id);
    }

    public async Task<PagedResult<ExecutionRecord>> QueryAsync(ExecuteHistoryQuery query)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize <= 0 ? 20 : query.PageSize, 1, 200);
        RefAsync<int> total = new();

        var records = await BuildFilterQuery(query)
            .OrderBy(r => r.StartTime, OrderByType.Desc)
            .ToPageListAsync(page, pageSize, total);

        return new PagedResult<ExecutionRecord>
        {
            Items = records,
            Total = total.Value,
            Page = page,
            PageSize = pageSize,
        };
    }

    public Task<List<ExecutionRecord>> GetRecentAsync(int count)
    {
        return db.Queryable<ExecutionRecord>()
            .OrderBy(r => r.StartTime, OrderByType.Desc)
            .Take(count)
            .ToListAsync();
    }

    public Task<List<ExecutionRecord>> GetRecentFailuresAsync(int count)
    {
        return db.Queryable<ExecutionRecord>()
            .Where(r => r.Status != (int)ExecutionStatus.Success)
            .OrderBy(r => r.StartTime, OrderByType.Desc)
            .Take(count)
            .ToListAsync();
    }

    public Task<int> CountTodayAsync()
    {
        return db.Queryable<ExecutionRecord>().CountAsync(r => r.StartTime >= DateTime.Today);
    }

    public Task<int> CountTodayFailuresAsync()
    {
        return db.Queryable<ExecutionRecord>()
            .CountAsync(r => r.StartTime >= DateTime.Today && r.Status != (int)ExecutionStatus.Success);
    }

    public Task ClearAsync(int? olderThanDays)
    {
        return olderThanDays.HasValue
            ? db.Deleteable<ExecutionRecord>()
                .Where(r => r.StartTime < DateTime.Today.AddDays(-olderThanDays.Value))
                .ExecuteCommandAsync()
            : db.Deleteable<ExecutionRecord>().ExecuteCommandAsync();
    }

    private ISugarQueryable<ExecutionRecord> BuildFilterQuery(ExecuteHistoryQuery query)
    {
        var sql = db.Queryable<ExecutionRecord>();
        if (query.Source.HasValue)
        {
            sql = sql.Where(r => r.Source == (int)query.Source.Value);
        }
        if (query.Status.HasValue)
        {
            sql = sql.Where(r => r.Status == (int)query.Status.Value);
        }
        if (query.CommandId.HasValue)
        {
            sql = sql.Where(r => r.CommandId == query.CommandId.Value);
        }
        if (query.ScheduleTaskId.HasValue)
        {
            sql = sql.Where(r => r.ScheduleTaskId == query.ScheduleTaskId.Value);
        }
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            sql = sql.Where(r => r.CommandName.Contains(keyword) || r.CommandText.Contains(keyword));
        }
        return sql;
    }
}
