using SqlSugar;

namespace LinuxWebTool.Infrastructure.Persistence.Entities;

/// <summary>一次指令执行的历史记录（调用历史 / 定时任务执行日志）。</summary>
[SugarTable("execution_record")]
public class ExecutionRecord
{
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>执行来源：0=Manual 1=Schedule 2=Quick。</summary>
    public int Source { get; set; }

    [SugarColumn(IsNullable = true)]
    public Guid? CommandId { get; set; }

    [SugarColumn(IsNullable = true)]
    public Guid? ScheduleTaskId { get; set; }

    [SugarColumn(ColumnDataType = "nvarchar(100)")]
    public string CommandName { get; set; } = string.Empty;

    [SugarColumn(ColumnDataType = "nvarchar(4000)")]
    public string CommandText { get; set; } = string.Empty;

    /// <summary>结果状态：0=Success 1=Failure 2=Timeout 3=Cancelled。</summary>
    public int Status { get; set; }

    [SugarColumn(IsNullable = true)]
    public int? ExitCode { get; set; }

    [SugarColumn(ColumnDataType = "nvarchar(4000)")]
    public string Output { get; set; } = string.Empty;

    [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(4000)")]
    public string ErrorOutput { get; set; } = string.Empty;

    public long DurationMs { get; set; }

    public bool TimedOut { get; set; }

    public bool Truncated { get; set; }

    [SugarColumn(ColumnDataType = "nvarchar(50)")]
    public string TriggerBy { get; set; } = string.Empty;

    public DateTime StartTime { get; set; } = DateTime.Now;

    [SugarColumn(IsNullable = true)]
    public DateTime? EndTime { get; set; }
}
