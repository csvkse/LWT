using SqlSugar;

namespace LinuxWebTool.Infrastructure.Persistence;

/// <summary>操作日志仓储。</summary>
public class OperationLogStore(ISqlSugarClient db)
{
    public async Task<Guid> InsertAsync(OperationLog log)
    {
        log.Id = Guid.NewGuid();
        log.Time = DateTime.Now;
        await db.Insertable(log).ExecuteCommandAsync();
        return log.Id;
    }

    public async Task<PagedResult<OperationLog>> QueryAsync(OperationLogQuery query)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize <= 0 ? 20 : query.PageSize, 1, 200);
        RefAsync<int> total = new();

        var sql = db.Queryable<OperationLog>();
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            sql = sql.Where(l => l.Action.Contains(keyword)
                || l.TargetName.Contains(keyword)
                || (l.Detail != null && l.Detail.Contains(keyword)));
        }

        var items = await sql
            .OrderBy(l => l.Time, OrderByType.Desc)
            .ToPageListAsync(page, pageSize, total);

        return new PagedResult<OperationLog>
        {
            Items = items,
            Total = total.Value,
            Page = page,
            PageSize = pageSize,
        };
    }
}
