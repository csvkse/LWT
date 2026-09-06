namespace LinuxWebTool.Contracts.Models;

public sealed record LoginResult(string Token, DateTime ExpiresAt, string UserName);

/// <summary>指令执行响应（含落库的执行记录 Id）。</summary>
public sealed record ExecuteResult
{
    public Guid RecordId { get; init; }
    public bool Success { get; init; }
    public int? ExitCode { get; init; }
    public string StandardOutput { get; init; } = string.Empty;
    public string ErrorOutput { get; init; } = string.Empty;
    public long DurationMs { get; init; }
    public bool TimedOut { get; init; }
    public bool Truncated { get; init; }
    public ExecutionStatus Status { get; init; }
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
}

public sealed record PagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = [];
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
}

public sealed record OverviewResult
{
    public int CommandCount { get; init; }
    public int ScheduleCount { get; init; }
    public int EnabledScheduleCount { get; init; }
    public int TodayExecutions { get; init; }
    public int TodayFailures { get; init; }
    public IReadOnlyList<ExecutionRecordBrief> RecentExecutions { get; init; } = [];
    public IReadOnlyList<ExecutionRecordBrief> RecentFailures { get; init; } = [];
    public IReadOnlyList<ScheduleBrief> NextRuns { get; init; } = [];
}

public sealed record ExecutionRecordBrief
{
    public Guid Id { get; init; }
    public DateTime StartTime { get; init; }
    public ExecutionSource Source { get; init; }
    public string CommandName { get; init; } = string.Empty;
    public string CommandText { get; init; } = string.Empty;
    public ExecutionStatus Status { get; init; }
    public int? ExitCode { get; init; }
    public long DurationMs { get; init; }
}

public sealed record ScheduleBrief
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string CronExpression { get; init; } = string.Empty;
    public DateTime? NextRunTime { get; init; }
}
