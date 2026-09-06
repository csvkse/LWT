using SqlSugar;

namespace LinuxWebTool.Infrastructure.Persistence.Entities;

/// <summary>定时任务：按 Cron 表达式调度执行已保存的指令。</summary>
[SugarTable("schedule_task")]
public class ScheduleTask
{
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [SugarColumn(ColumnDataType = "nvarchar(100)")]
    public string Name { get; set; } = string.Empty;

    public Guid CommandId { get; set; }

    [SugarColumn(ColumnDataType = "nvarchar(100)")]
    public string CronExpression { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    [SugarColumn(IsNullable = true)]
    public Guid? GroupId { get; set; }

    public bool IsPinned { get; set; }

    public int SortOrder { get; set; }

    [SugarColumn(IsNullable = true)]
    public int? TimeoutSeconds { get; set; }

    /// <summary>脚本位置参数原始串（引号感知拆分为 $1 $2...；定时执行使用固定参数）。</summary>
    [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(500)")]
    public string? Arguments { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? LastRunTime { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? NextRunTime { get; set; }

    public DateTime CreateTime { get; set; } = DateTime.Now;

    public DateTime UpdateTime { get; set; } = DateTime.Now;
}
