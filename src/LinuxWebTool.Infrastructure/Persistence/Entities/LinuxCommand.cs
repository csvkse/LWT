using SqlSugar;

namespace LinuxWebTool.Infrastructure.Persistence.Entities;

/// <summary>已保存的 Linux 指令。</summary>
[SugarTable("linux_command")]
public class LinuxCommand
{
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [SugarColumn(ColumnDataType = "nvarchar(100)")]
    public string Name { get; set; } = string.Empty;

    [SugarColumn(ColumnDataType = "nvarchar(4000)")]
    public string CommandText { get; set; } = string.Empty;

    /// <summary>0=命令行 1=Bash 脚本（CommandText 为脚本正文，SQLite TEXT 亲和无长度限制）。</summary>
    public int ScriptType { get; set; }

    [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(500)")]
    public string? Description { get; set; }

    [SugarColumn(IsNullable = true)]
    public Guid? GroupId { get; set; }

    public bool IsPinned { get; set; }

    public int SortOrder { get; set; }

    /// <summary>执行超时秒数；为空时使用 Shell 默认值。</summary>
    [SugarColumn(IsNullable = true)]
    public int? TimeoutSeconds { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? LastExecTime { get; set; }

    public DateTime CreateTime { get; set; } = DateTime.Now;

    public DateTime UpdateTime { get; set; } = DateTime.Now;
}
