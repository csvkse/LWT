using LinuxWebTool.Contracts.Features.Commands.Contracts;

namespace LinuxWebTool.Contracts.Shared.Contracts;

public sealed record LoginRequest(string UserName, string Password);

/// <summary>修改管理员凭据请求：新用户名 / 新密码至少提供一项。</summary>
public sealed record ChangeCredentialRequest
{
    public required string CurrentPassword { get; init; }
    public string? NewUserName { get; init; }
    public string? NewPassword { get; init; }
}

public sealed record SaveCommandRequest(
    string Name,
    string CommandText,
    ScriptType ScriptType,
    string? Description,
    Guid? GroupId,
    bool IsPinned,
    int? TimeoutSeconds,
    string? DefaultTargetHostId = null,
    string ExecutionTarget = "container");

public sealed record ExecuteCommandRequest(string? Arguments = null, string? TargetHostId = null);
public sealed record QuickExecuteRequest(string CommandText, int? TimeoutSeconds = null, string? TargetHostId = null, string Target = "container");

public sealed record SaveScheduleRequest(
    string Name,
    Guid CommandId,
    string CronExpression,
    bool Enabled,
    Guid? GroupId,
    bool IsPinned,
    int? TimeoutSeconds,
    string? Arguments,
    TargetHostMode TargetHostMode = TargetHostMode.Local,
    string? TargetHostId = null,
    string? TargetHostIds = null,
    FailurePolicy FailurePolicy = FailurePolicy.FailFast,
    TargetBizType TargetBizType = TargetBizType.Command,
    string? WorkflowId = null,
    string ExecutionTarget = "container");

public sealed record SaveGroupRequest(string Name, GroupBizType BizType, int SortOrder);

public sealed record ExecuteHistoryQuery
{
    public int? Source { get; init; }
    public int? Status { get; init; }
    public Guid? CommandId { get; init; }
    public Guid? ScheduleTaskId { get; init; }
    public string? TargetHostId { get; init; }
    public string? BatchId { get; init; }
    public string? WorkflowId { get; init; }
    public string? Keyword { get; init; }
    public int? Page { get; init; } = 1;
    public int? PageSize { get; init; } = 20;
}

public sealed record BatchExecutionItem(
    Guid? CommandId,
    string CommandText,
    int ScriptType,
    string? Arguments,
    int TimeoutSeconds,
    string? TargetHostId,
    string? Name = null);

/// <summary>多指令批量执行请求。</summary>
public sealed record BatchExecutionRequest
{
    public IReadOnlyList<Guid> CommandIds { get; init; } = [];
    public IReadOnlyList<BatchExecutionItem> Commands { get; init; } = [];
    /// <summary>统一目标主机 Id（为 null 表示本地）。</summary>
    public string? TargetHostId { get; init; }
    /// <summary>是否尊重各指令自身的推荐目标主机。</summary>
    public bool RespectIndividualHost { get; init; }
    /// <summary>执行模式：Sequential(串行), Parallel(并行)。</summary>
    public ExecutionMode ExecutionMode { get; init; } = ExecutionMode.Sequential;
    /// <summary>失败策略：FailFast(遇错即停), Continue(容错继续)。</summary>
    public FailurePolicy FailurePolicy { get; init; } = FailurePolicy.FailFast;
    /// <summary>最大并行度。</summary>
    public int MaxConcurrency { get; init; } = 5;
    /// <summary>全局超时秒数（可选）。</summary>
    public int? TimeoutSeconds { get; init; }
}

/// <summary>多指令批量执行响应。</summary>
public sealed record BatchExecutionResponse
{
    public string BatchId { get; init; } = string.Empty;
    public ExecutionStatus OverallStatus { get; init; }
    public long DurationMs { get; init; }
    public IReadOnlyList<ExecuteResult> Results { get; init; } = [];

    public BatchExecutionResponse() { }

    public BatchExecutionResponse(string batchId, IReadOnlyList<ExecuteResult> results, ExecutionStatus overallStatus, long durationMs)
    {
        BatchId = batchId;
        Results = results;
        OverallStatus = overallStatus;
        DurationMs = durationMs;
    }
}

public sealed record OperationLogQuery
{
    public int? Page { get; init; } = 1;
    public int? PageSize { get; init; } = 20;
    public string? Keyword { get; init; }
}
