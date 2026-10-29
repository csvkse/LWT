namespace LinuxWebTool.Infrastructure.Shared.Persistence.Entities;

/// <summary>一次指令执行的历史记录（调用历史 / 定时任务执行日志）。</summary>
public class ExecutionRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>执行来源：0=Manual 1=Schedule 2=Quick 3=Batch 4=Mcp。</summary>
    public int Source { get; set; }

    public Guid? CommandId { get; set; }

    public Guid? ScheduleTaskId { get; set; }

    public string CommandName { get; set; } = string.Empty;

    public string CommandText { get; set; } = string.Empty;

    /// <summary>结果状态：0=Success 1=Failure 2=Timeout 3=Cancelled 4=Skipped。</summary>
    public int Status { get; set; }

    public int? ExitCode { get; set; }

    public string Output { get; set; } = string.Empty;

    public string ErrorOutput { get; set; } = string.Empty;

    public long DurationMs { get; set; }

    public bool TimedOut { get; set; }

    public bool Truncated { get; set; }

    public string TriggerBy { get; set; } = string.Empty;

    /// <summary>目标环境类型：0=Local, 1=RemoteHost。</summary>
    public int TargetType { get; set; }

    /// <summary>目标主机 ID。</summary>
    public string? TargetHostId { get; set; }

    /// <summary>目标主机快照名。</summary>
    public string? TargetHostName { get; set; }

    /// <summary>批次号（多主机分发或多指令批量执行关联）。</summary>
    public string? BatchId { get; set; }

    /// <summary>流水线步骤序号（0=独立单次，1..N=流水线步骤）。</summary>
    public int StepOrder { get; set; }

    /// <summary>关联工作流 ID（若由工作流触发）。</summary>
    public string? WorkflowId { get; set; }

    public DateTime StartTime { get; set; } = DateTime.Now;

    public DateTime? EndTime { get; set; }
}
