using Dapper;
using LinuxWebTool.Infrastructure.Persistence.Entities;

namespace LinuxWebTool.Infrastructure.Persistence;

/// <summary>执行历史（调用历史 / 定时任务执行日志）仓储。</summary>
[DapperAot]
public partial class ExecutionStore(DbConnectionFactory factory)
{
    public async Task<Guid> InsertAsync(ExecutionRecord record)
    {
        using var db = factory.CreateConnection();
        record.Id = Guid.NewGuid();
        var sql = @"
            INSERT INTO execution_record (Id, Source, CommandId, ScheduleTaskId, CommandName, CommandText, Status, ExitCode, Output, ErrorOutput, DurationMs, TimedOut, Truncated, TriggerBy, StartTime, EndTime)
            VALUES (@Id, @Source, @CommandId, @ScheduleTaskId, @CommandName, @CommandText, @Status, @ExitCode, @Output, @ErrorOutput, @DurationMs, @TimedOut, @Truncated, @TriggerBy, @StartTime, @EndTime)";
        await db.ExecuteAsync(sql, record);
        return record.Id;
    }

    public async Task<ExecutionRecord?> GetByIdAsync(Guid id)
    {
        using var db = factory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<ExecutionRecord>("SELECT * FROM execution_record WHERE Id = @Id", new { Id = id });
    }

    public async Task<PagedResult<ExecutionRecord>> QueryAsync(ExecuteHistoryQuery query)
    {
        using var db = factory.CreateConnection();
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize <= 0 ? 20 : query.PageSize, 1, 200);
        
        var whereClause = "WHERE 1=1";
        var parameters = new DynamicParameters();
        
        if (query.Source.HasValue)
        {
            whereClause += " AND Source = @Source";
            parameters.Add("Source", (int)query.Source.Value);
        }
        if (query.Status.HasValue)
        {
            whereClause += " AND Status = @Status";
            parameters.Add("Status", (int)query.Status.Value);
        }
        if (query.CommandId.HasValue)
        {
            whereClause += " AND CommandId = @CommandId";
            parameters.Add("CommandId", query.CommandId.Value);
        }
        if (query.ScheduleTaskId.HasValue)
        {
            whereClause += " AND ScheduleTaskId = @ScheduleTaskId";
            parameters.Add("ScheduleTaskId", query.ScheduleTaskId.Value);
        }
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            whereClause += " AND (CommandName LIKE @Keyword OR CommandText LIKE @Keyword)";
            parameters.Add("Keyword", $"%{keyword}%");
        }

        var countSql = $"SELECT COUNT(*) FROM execution_record {whereClause}";
        var total = await db.QueryFirstOrDefaultAsync<int>(countSql, parameters);

        var offset = (page - 1) * pageSize;
        var dataSql = $"SELECT * FROM execution_record {whereClause} ORDER BY StartTime DESC LIMIT @PageSize OFFSET @Offset";
        parameters.Add("PageSize", pageSize);
        parameters.Add("Offset", offset);

        var records = await db.QueryAsync<ExecutionRecord>(dataSql, parameters);

        return new PagedResult<ExecutionRecord>
        {
            Items = records.ToList(),
            Total = total,
            Page = page,
            PageSize = pageSize,
        };
    }

    public async Task<IEnumerable<ExecutionRecord>> GetRecentAsync(int count)
    {
        using var db = factory.CreateConnection();
        return await db.QueryAsync<ExecutionRecord>("SELECT * FROM execution_record ORDER BY StartTime DESC LIMIT @Count", new { Count = count });
    }

    public async Task<IEnumerable<ExecutionRecord>> GetRecentFailuresAsync(int count)
    {
        using var db = factory.CreateConnection();
        return await db.QueryAsync<ExecutionRecord>("SELECT * FROM execution_record WHERE Status != @Status ORDER BY StartTime DESC LIMIT @Count", new { Status = (int)ExecutionStatus.Success, Count = count });
    }

    public async Task<int> CountTodayAsync()
    {
        using var db = factory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<int>("SELECT COUNT(*) FROM execution_record WHERE StartTime >= @Today", new { Today = DateTime.Today });
    }

    public async Task<int> CountTodayFailuresAsync()
    {
        using var db = factory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<int>("SELECT COUNT(*) FROM execution_record WHERE StartTime >= @Today AND Status != @Status", new { Today = DateTime.Today, Status = (int)ExecutionStatus.Success });
    }

    public async Task ClearAsync(int? olderThanDays)
    {
        using var db = factory.CreateConnection();
        if (olderThanDays.HasValue)
        {
            var cutoff = DateTime.Today.AddDays(-olderThanDays.Value);
            await db.ExecuteAsync("DELETE FROM execution_record WHERE StartTime < @Cutoff", new { Cutoff = cutoff });
        }
        else
        {
            await db.ExecuteAsync("DELETE FROM execution_record");
        }
    }
}
