using SqlSugar;

namespace LinuxWebTool.Infrastructure.Persistence.Entities;

/// <summary>系统状态历史快照（后台 60s 采样一条，保留 7 天，供前端画曲线）。</summary>
[SugarTable("system_status_snapshot")]
public class SystemStatusSnapshot
{
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTime Time { get; set; } = DateTime.Now;

    /// <summary>CPU 使用率（0~100）。</summary>
    public double CpuUsage { get; set; }

    public double Load1 { get; set; }

    /// <summary>内存使用率（0~100）。</summary>
    public double MemUsage { get; set; }

    /// <summary>根分区（Windows 系统盘）使用率（0~100）。</summary>
    public double DiskRootUsage { get; set; }

    /// <summary>全网卡发送速率（字节/秒）。</summary>
    public long NetSentBps { get; set; }

    /// <summary>全网卡接收速率（字节/秒）。</summary>
    public long NetRecvBps { get; set; }
}
