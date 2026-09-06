namespace LinuxWebTool.Contracts.Models;

/// <summary>执行来源。</summary>
public enum ExecutionSource
{
    /// <summary>手动执行已保存指令。</summary>
    Manual = 0,
    /// <summary>定时任务调度执行。</summary>
    Schedule = 1,
    /// <summary>快速临时指令执行。</summary>
    Quick = 2,
}

/// <summary>执行结果状态。</summary>
public enum ExecutionStatus
{
    Success = 0,
    Failure = 1,
    Timeout = 2,
    Cancelled = 3,
}

/// <summary>分组业务类型：指令分组 / 定时任务分组。</summary>
public enum GroupBizType
{
    Command = 0,
    Schedule = 1,
}

/// <summary>指令内容类型：单行命令 / Bash 脚本（多行，走临时文件 + 位置参数执行）。</summary>
public enum ScriptType
{
    Command = 0,
    BashScript = 1,
}

/// <summary>一次 shell 执行请求。</summary>
public sealed record ShellRequest
{
    /// <summary>命令行内容（ScriptType=Command 时使用）。</summary>
    public string? CommandText { get; init; }

    /// <summary>脚本正文（ScriptText 非空时走脚本模式：写临时文件后 bash 执行）。</summary>
    public string? ScriptText { get; init; }

    /// <summary>脚本位置参数原始串（引号感知拆分后作为 $1 $2... 传入，不经二次 shell 解释）。</summary>
    public string? ScriptArguments { get; init; }

    /// <summary>为空时使用 Shell 默认超时（秒）。</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>为空时使用 Shell 默认工作目录。</summary>
    public string? WorkingDirectory { get; init; }
}

/// <summary>一次 shell 执行结果。</summary>
public sealed record ShellResult
{
    public int? ExitCode { get; init; }
    public string StandardOutput { get; init; } = string.Empty;
    public string ErrorOutput { get; init; } = string.Empty;
    public long DurationMs { get; init; }
    public bool TimedOut { get; init; }
    public bool Cancelled { get; init; }
    public bool Truncated { get; init; }
    public bool Started { get; init; }
    public string? StartFailure { get; init; }
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }

    public ExecutionStatus Status => TimedOut ? ExecutionStatus.Timeout
        : Cancelled ? ExecutionStatus.Cancelled
        : ExitCode == 0 ? ExecutionStatus.Success
        : ExecutionStatus.Failure;
}
