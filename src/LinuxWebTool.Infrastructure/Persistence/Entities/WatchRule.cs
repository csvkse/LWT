using SqlSugar;

namespace LinuxWebTool.Infrastructure.Persistence.Entities;

/// <summary>文件夹监听规则：发现新增 / 变更的匹配文件后自动入队转码。</summary>
[SugarTable("watch_rule")]
public class WatchRule
{
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [SugarColumn(ColumnDataType = "nvarchar(100)")]
    public string Name { get; set; } = string.Empty;

    /// <summary>监听目录（可为 SMB 挂载路径）。</summary>
    [SugarColumn(ColumnDataType = "nvarchar(1000)")]
    public string WatchPath { get; set; } = string.Empty;

    /// <summary>扩展名过滤（逗号分隔含点）；空 = 内置媒体扩展名全集。</summary>
    [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(500)")]
    public string? FilePatterns { get; set; }

    public Guid PresetId { get; set; }

    /// <summary>输出模式：0=替换 1=并存。</summary>
    public int OutputMode { get; set; }

    public bool Recursive { get; set; }

    /// <summary>扫描方式：0=轮询 1=文件系统事件。</summary>
    public int Mode { get; set; }

    /// <summary>轮询间隔秒数（最小 30）。</summary>
    public int PollSeconds { get; set; } = 300;

    public bool Enabled { get; set; } = true;

    [SugarColumn(IsNullable = true)]
    public DateTime? LastScanTime { get; set; }

    public DateTime CreateTime { get; set; } = DateTime.Now;

    public DateTime UpdateTime { get; set; } = DateTime.Now;
}
