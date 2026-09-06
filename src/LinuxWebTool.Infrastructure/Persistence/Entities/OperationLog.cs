using SqlSugar;

namespace LinuxWebTool.Infrastructure.Persistence.Entities;

/// <summary>操作日志（增删改、执行、登录等行为审计）。</summary>
[SugarTable("operation_log")]
public class OperationLog
{
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTime Time { get; set; } = DateTime.Now;

    [SugarColumn(ColumnDataType = "nvarchar(100)")]
    public string Action { get; set; } = string.Empty;

    [SugarColumn(ColumnDataType = "nvarchar(50)")]
    public string TargetType { get; set; } = string.Empty;

    [SugarColumn(ColumnDataType = "nvarchar(100)")]
    public string TargetName { get; set; } = string.Empty;

    [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(1000)")]
    public string? Detail { get; set; }

    [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(50)")]
    public string? ClientIp { get; set; }

    public bool Success { get; set; } = true;
}
