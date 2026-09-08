using SqlSugar;

namespace LinuxWebTool.Infrastructure.Persistence.Entities;

/// <summary>
/// 系统状态历史磁盘挂载点快照（每次采样记录每个挂载点的使用情况）。
/// 与 SystemStatusProcessSnapshot / SystemStatusNetSnapshot 共用同一采样时间戳（Time）。
/// </summary>
[SugarTable("system_status_disk_snapshot")]
public class SystemStatusDiskSnapshot
{
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>共用采样时间（与整机 / 进程 / 网络快照同刻）。</summary>
    public DateTime Time { get; set; } = DateTime.Now;

    /// <summary>挂载点。</summary>
    public string Mount { get; set; } = string.Empty;

    public string FileSystem { get; set; } = string.Empty;

    /// <summary>使用率（0~100）。</summary>
    public double UsagePercent { get; set; }

    public long TotalBytes { get; set; }
    public long UsedBytes { get; set; }
    public long FreeBytes { get; set; }
}
