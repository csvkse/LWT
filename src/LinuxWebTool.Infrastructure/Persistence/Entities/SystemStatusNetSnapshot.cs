using SqlSugar;

namespace LinuxWebTool.Infrastructure.Persistence.Entities;

/// <summary>
/// 系统状态历史网卡快照（每次采样记录每个网卡的收发速率）。
/// 与 SystemStatusProcessSnapshot / SystemStatusDiskSnapshot 共用同一采样时间戳（Time）。
/// </summary>
[SugarTable("system_status_net_snapshot")]
public class SystemStatusNetSnapshot
{
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>共用采样时间（与整机 / 进程 / 磁盘快照同刻）。</summary>
    public DateTime Time { get; set; } = DateTime.Now;

    /// <summary>网卡名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>发送速率（字节/秒）。</summary>
    public long SentBytesPerSec { get; set; }

    /// <summary>接收速率（字节/秒）。</summary>
    public long RecvBytesPerSec { get; set; }
}
