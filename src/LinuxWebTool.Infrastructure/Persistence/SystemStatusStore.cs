using Dapper;
using LinuxWebTool.Contracts.Models;
using LinuxWebTool.Infrastructure.Persistence.Entities;

namespace LinuxWebTool.Infrastructure.Persistence;

/// <summary>系统状态快照仓储（写入 / 时间窗口查询 / 过期清理）。</summary>
[DapperAot]
public partial class SystemStatusStore(DbConnectionFactory factory)
{
    public async Task InsertAsync(SystemStatusSnapshot snapshot)
    {
        snapshot.Time = DateTime.Now;
        using var db = factory.CreateConnection();
        var sql = @"
INSERT INTO system_status_snapshot (Id, Time, CpuUsage, Load1, MemUsage, DiskRootUsage, NetSentBps, NetRecvBps)
VALUES (@Id, @Time, @CpuUsage, @Load1, @MemUsage, @DiskRootUsage, @NetSentBps, @NetRecvBps)";
        await db.ExecuteAsync(sql, snapshot);
    }

    /// <summary>以显式时间戳插入（与进程 / 磁盘 / 网络快照共用同一采样时刻）。</summary>
    public async Task InsertAsync(SystemStatusSnapshot snapshot, DateTime time)
    {
        snapshot.Time = time;
        using var db = factory.CreateConnection();
        var sql = @"
INSERT INTO system_status_snapshot (Id, Time, CpuUsage, Load1, MemUsage, DiskRootUsage, NetSentBps, NetRecvBps)
VALUES (@Id, @Time, @CpuUsage, @Load1, @MemUsage, @DiskRootUsage, @NetSentBps, @NetRecvBps)";
        await db.ExecuteAsync(sql, snapshot);
    }

    /// <summary>查询最近 hours 小时的序列点（时间正序，供曲线渲染）。</summary>
    public async Task<IEnumerable<StatusSnapshotPoint>> QueryAsync(int hours)
    {
        hours = Math.Clamp(hours, 1, 24 * 30);
        var since = DateTime.Now.AddHours(-hours);
        using var db = factory.CreateConnection();
        var sql = @"
SELECT *
FROM system_status_snapshot
WHERE Time >= @Since
ORDER BY Time";
        var rows = await db.QueryAsync<SystemStatusSnapshot>(sql, new { Since = since });
        return rows.Select(ToPoint);
    }

    public async Task ClearOlderThan(DateTime cutoff)
    {
        using var db = factory.CreateConnection();
        var sql = "DELETE FROM system_status_snapshot WHERE Time < @Cutoff";
        await db.ExecuteAsync(sql, new { Cutoff = cutoff });
    }

    /// <summary>
    /// 判断最近 window 内是否存在任一资源越阈值的整机快照（异常窗口判定）。
    /// 整机快照按 SamplingIntervalSeconds 采样，足以反映 CPU/内存/磁盘/网络是否越阈。
    /// </summary>
    public async Task<bool> IsRecentAboveAsync(
        TimeSpan window,
        double cpuThreshold,
        double memThreshold,
        double diskThreshold,
        long netThresholdBps)
    {
        var since = DateTime.Now.Add(-window);
        using var db = factory.CreateConnection();
        var sql = @"
SELECT *
FROM system_status_snapshot
WHERE Time >= @Since";
        var rows = await db.QueryAsync<SystemStatusSnapshot>(sql, new { Since = since });
        return rows.Any(s =>
            s.CpuUsage >= cpuThreshold ||
            s.MemUsage >= memThreshold ||
            (diskThreshold > 0 && s.DiskRootUsage >= diskThreshold) ||
            (netThresholdBps > 0 && (s.NetSentBps >= netThresholdBps || s.NetRecvBps >= netThresholdBps)));
    }

    private static StatusSnapshotPoint ToPoint(SystemStatusSnapshot s) => new()
    {
        Time = s.Time,
        CpuUsage = s.CpuUsage,
        Load1 = s.Load1,
        MemUsage = s.MemUsage,
        DiskRootUsage = s.DiskRootUsage,
        NetSentBps = s.NetSentBps,
        NetRecvBps = s.NetRecvBps,
    };
}
