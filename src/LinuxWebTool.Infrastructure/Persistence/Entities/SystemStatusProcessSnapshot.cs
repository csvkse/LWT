using SqlSugar;

namespace LinuxWebTool.Infrastructure.Persistence.Entities;

/// <summary>
/// 系统状态历史进程序列快照（每次采样记录当前 Top 进程）。
/// 与 SystemStatusDiskSnapshot / SystemStatusNetSnapshot 共用同一采样时间戳（Time）。
/// </summary>
[SugarTable("system_status_process_snapshot")]
public class SystemStatusProcessSnapshot
{
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>共用采样时间（与整机 / 磁盘 / 网络快照同刻）。</summary>
    public DateTime Time { get; set; } = DateTime.Now;

    public int Pid { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>CPU 占用（0~100，单进程相对整机）。</summary>
    public double CpuPercent { get; set; }

    /// <summary>内存占用（0~100）。</summary>
    public double MemPercent { get; set; }

    public long MemBytes { get; set; }

    /// <summary>磁盘读取速率（字节/秒，基于 /proc/&lt;pid&gt;/io read_bytes 差值；无法采集为 0）。</summary>
    public long DiskReadBps { get; set; }

    /// <summary>磁盘写入速率（字节/秒，基于 /proc/&lt;pid&gt;/io write_bytes 差值；无法采集为 0）。</summary>
    public long DiskWriteBps { get; set; }

    /// <summary>网络发送速率（字节/秒，nethogs tracemode 采集；未安装/无特权为 0）。</summary>
    public long NetSentBps { get; set; }

    /// <summary>网络接收速率（字节/秒，nethogs tracemode 采集；未安装/无特权为 0）。</summary>
    public long NetRecvBps { get; set; }
}
