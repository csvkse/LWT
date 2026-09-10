using Dapper;
using LinuxWebTool.Contracts.Models;
using LinuxWebTool.Infrastructure.Persistence.Entities;

namespace LinuxWebTool.Infrastructure.Persistence;

/// <summary>系统状态进程序列历史快照仓储（写入 / 时间窗口查询 / 过期清理）。</summary>
[DapperAot]
public partial class SystemStatusProcessStore(DbConnectionFactory factory)
{
    public async Task InsertAsync(IEnumerable<SystemStatusProcessSnapshot> snapshots)
    {
        var batch = snapshots as SystemStatusProcessSnapshot[] ?? snapshots.ToArray();
        if (batch.Length > 0)
        {
            using var db = factory.CreateConnection();
            var sql = @"
INSERT INTO system_status_process_snapshot (Id, Time, Pid, Name, CpuPercent, MemPercent, MemBytes, DiskReadBps, DiskWriteBps, NetSentBps, NetRecvBps)
VALUES (@Id, @Time, @Pid, @Name, @CpuPercent, @MemPercent, @MemBytes, @DiskReadBps, @DiskWriteBps, @NetSentBps, @NetRecvBps)";
            await db.ExecuteAsync(sql, batch);
        }
    }

    /// <summary>查询最近 hours 小时的进程序列点（时间正序，供曲线 / 明细渲染）。</summary>
    public async Task<IEnumerable<ProcessSnapshotPoint>> QueryAsync(int hours)
    {
        hours = Math.Clamp(hours, 1, 24 * 30);
        var since = DateTime.Now.AddHours(-hours);
        using var db = factory.CreateConnection();
        var sql = @"
SELECT *
FROM system_status_process_snapshot
WHERE Time >= @Since
ORDER BY Time";
        var rows = await db.QueryAsync<SystemStatusProcessSnapshot>(sql, new { Since = since });
        return rows.Select(ToPoint);
    }

    /// <summary>查询指定时间近邻（前后 tolerance 分钟）内的进程序列点，用于展开某个采样点明细。</summary>
    public async Task<IEnumerable<ProcessSnapshotPoint>> QueryAroundAsync(DateTime time, int toleranceMinutes = 5)
    {
        var since = time.AddMinutes(-toleranceMinutes);
        var until = time.AddMinutes(toleranceMinutes);
        using var db = factory.CreateConnection();
        var sql = @"
SELECT *
FROM system_status_process_snapshot
WHERE Time >= @Since AND Time <= @Until
ORDER BY Time";
        var rows = await db.QueryAsync<SystemStatusProcessSnapshot>(sql, new { Since = since, Until = until });
        return rows.Select(ToPoint);
    }

    public async Task ClearOlderThan(DateTime cutoff)
    {
        using var db = factory.CreateConnection();
        var sql = "DELETE FROM system_status_process_snapshot WHERE Time < @Cutoff";
        await db.ExecuteAsync(sql, new { Cutoff = cutoff });
    }

    private static ProcessSnapshotPoint ToPoint(SystemStatusProcessSnapshot s) => new()
    {
        Time = s.Time,
        Pid = s.Pid,
        Name = s.Name,
        CpuPercent = s.CpuPercent,
        MemPercent = s.MemPercent,
        MemBytes = s.MemBytes,
        DiskReadBps = s.DiskReadBps,
        DiskWriteBps = s.DiskWriteBps,
        NetSentBps = s.NetSentBps,
        NetRecvBps = s.NetRecvBps,
    };
}
