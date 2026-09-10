using Dapper;
using LinuxWebTool.Infrastructure.Persistence.Entities;

namespace LinuxWebTool.Infrastructure.Persistence;

/// <summary>操作日志仓储。</summary>
[DapperAot]
public partial class OperationLogStore(DbConnectionFactory factory)
{
    public async Task<Guid> InsertAsync(OperationLog log)
    {
        log.Id = Guid.NewGuid();
        log.Time = DateTime.Now;
        using var db = factory.CreateConnection();
        var sql = @"
INSERT INTO operation_log (Id, Time, Action, TargetType, TargetName, Detail, ClientIp, Success) 
VALUES (@Id, @Time, @Action, @TargetType, @TargetName, @Detail, @ClientIp, @Success)";
        await db.ExecuteAsync(sql, log);
        return log.Id;
    }

    public async Task<PagedResult<OperationLog>> QueryAsync(OperationLogQuery query)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize <= 0 ? 20 : query.PageSize, 1, 200);
        
        using var db = factory.CreateConnection();
        var whereClause = "";
        string? keyword = null;
        
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            keyword = $"%{query.Keyword.Trim()}%";
            whereClause = "WHERE Action LIKE @Keyword OR TargetName LIKE @Keyword OR Detail LIKE @Keyword";
        }

        var countSql = $"SELECT COUNT(1) FROM operation_log {whereClause}";
        var total = await db.QueryFirstOrDefaultAsync<int>(countSql, new { Keyword = keyword });

        var dataSql = $"SELECT * FROM operation_log {whereClause} ORDER BY Time DESC LIMIT @Limit OFFSET @Offset";
        var items = await db.QueryAsync<OperationLog>(dataSql, new { Keyword = keyword, Limit = pageSize, Offset = (page - 1) * pageSize });

        return new PagedResult<OperationLog>
        {
            Items = items.ToList(),
            Total = total,
            Page = page,
            PageSize = pageSize,
        };
    }
}
