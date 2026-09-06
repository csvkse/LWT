namespace LinuxWebTool.Contracts.Models;

public sealed record LoginRequest(string UserName, string Password);

/// <summary>修改管理员凭据请求：新用户名 / 新口令至少提供一项。</summary>
public sealed record ChangeCredentialRequest
{
    public required string CurrentPassword { get; init; }
    public string? NewUserName { get; init; }
    public string? NewPassword { get; init; }
}

public sealed record SaveCommandRequest
{
    public required string Name { get; init; }
    public required string CommandText { get; init; }
    /// <summary>内容类型：0=命令行 1=Bash 脚本（CommandText 为脚本正文）。</summary>
    public ScriptType ScriptType { get; init; }
    public string? Description { get; init; }
    public Guid? GroupId { get; init; }
    public bool IsPinned { get; init; }
    public int? TimeoutSeconds { get; init; }
}

/// <summary>执行已保存指令的请求体（可携带脚本位置参数）。</summary>
public sealed record ExecuteCommandRequest
{
    /// <summary>位置参数原始串（引号感知拆分为 $1 $2...），如：nginx restart。</summary>
    public string? Arguments { get; init; }
}

public sealed record QuickExecuteRequest
{
    public required string CommandText { get; init; }
    public int? TimeoutSeconds { get; init; }
}

public sealed record SaveScheduleRequest
{
    public required string Name { get; init; }
    public required Guid CommandId { get; init; }
    public required string CronExpression { get; init; }
    public bool Enabled { get; init; } = true;
    public Guid? GroupId { get; init; }
    public bool IsPinned { get; init; }
    public int? TimeoutSeconds { get; init; }
    /// <summary>脚本位置参数原始串（仅脚本类型生效，调度执行时作为 $1 $2... 传入）。</summary>
    public string? Arguments { get; init; }
}

public sealed record SaveGroupRequest
{
    public required string Name { get; init; }
    public GroupBizType BizType { get; init; }
    public int SortOrder { get; init; }
}

public sealed record ExecuteHistoryQuery
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    public ExecutionSource? Source { get; init; }
    public ExecutionStatus? Status { get; init; }
    public Guid? CommandId { get; init; }
    public Guid? ScheduleTaskId { get; init; }
    public string? Keyword { get; init; }
}

public sealed record OperationLogQuery
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    public string? Keyword { get; init; }
}
