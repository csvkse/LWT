using SqlSugar;

namespace LinuxWebTool.Infrastructure.Persistence.Entities;

/// <summary>分组（指令 / 定时任务共用一张表，按 BizType 区分）。</summary>
[SugarTable("command_group")]
public class CommandGroup
{
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [SugarColumn(ColumnDataType = "nvarchar(100)")]
    public string Name { get; set; } = string.Empty;

    /// <summary>0=指令分组 1=定时任务分组。</summary>
    public int BizType { get; set; }

    public int SortOrder { get; set; }

    public DateTime CreateTime { get; set; } = DateTime.Now;
}
