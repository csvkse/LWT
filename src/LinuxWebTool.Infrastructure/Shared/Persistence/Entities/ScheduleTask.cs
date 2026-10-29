namespace LinuxWebTool.Infrastructure.Shared.Persistence.Entities;

/// <summary>定时任务：按 Cron 表达式调度执行已保存的指令或工作流。</summary>
public class ScheduleTask
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public Guid CommandId { get; set; }

    public string CronExpression { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public Guid? GroupId { get; set; }

    public bool IsPinned { get; set; }

    public int SortOrder { get; set; }

    public int? TimeoutSeconds { get; set; }

    /// <summary>脚本位置参数原始串（引号感知拆分为 $1 $2...；定时执行使用固定参数）。</summary>
    public string? Arguments { get; set; }

    /// <summary>执行主机模式：0=Local, 1=SingleRemote, 2=MultiRemote。</summary>
    public int TargetHostMode { get; set; }

    /// <summary>目标主机 Id（单主机模式）。</summary>
    public string? TargetHostId { get; set; }

    /// <summary>目标主机 Id 列表（JSON 数组，多主机模式）。</summary>
    public string? TargetHostIds { get; set; }

    /// <summary>失败处理策略：0=FailFast(遇错即停), 1=Continue(容错继续)。</summary>
    public int FailurePolicy { get; set; }

    /// <summary>调度目标业务类型：0=Command(指令), 1=Workflow(工作流)。</summary>
    public int TargetBizType { get; set; }

    /// <summary>关联工作流 Id（当 TargetBizType=1 时有效）。</summary>
    public string? WorkflowId { get; set; }

    public DateTime? LastRunTime { get; set; }

    public DateTime? NextRunTime { get; set; }

    public DateTime CreateTime { get; set; } = DateTime.Now;

    public DateTime UpdateTime { get; set; } = DateTime.Now;
}
