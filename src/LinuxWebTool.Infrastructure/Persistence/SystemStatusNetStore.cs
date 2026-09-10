using Dapper;
using LinuxWebTool.Contracts.Models;
using LinuxWebTool.Infrastructure.Persistence.Entities;

namespace LinuxWebTool.Infrastructure.Persistence;

/// <summary>系统状态网卡历史快照仓储（写入 / 时间窗口查询 / 过期清理）。</summary>
[DapperAot]
public partial class SystemStatusNetStore(DbConnectionFactory factory)
{
    public async Task InsertAsync(IEnumerable<SystemStatusNetSnapshot> snapshots)
    {
        var batch = snapshots as SystemStatusNetSnapshot[] ?? snapshots.ToArray();
        if (batch.Length > 0)
        {
            using var db = factory.CreateConnection();
            var sql = @"
INSERT INTO system_status_net_snapshot (Id, Time, Name, SentBytesPerSec, RecvBytesPerSec)
VALUES (@Id, @Time, @Name, @SentBytesPerSec, @RecvBytesPerSec)";
            await db.ExecuteAsync(sql, batch);
        }
    }

    /// <summary>查询最近 hours 小时的网卡序列点（时间正序，供曲线渲染）。</summary>
    public async Task<IEnumerable<NetSnapshotPoint>> QueryAsync(int hours)
    {
        hours = Math.Clamp(hours, 1, 24 * 30);
        var since = DateTime.Now.AddHours(-hours);
        using var db = factory.CreateConnection();
        var sql = @"
SELECT *
FROM system_status_net_snapshot
WHERE Time >= @Since
ORDER BY Time";
        var rows = await db.QueryAsync<SystemStatusNetSnapshot>(sql, new { Since = since });
        return rows.Select(ToPoint);
    }

    public async Task ClearOlderThan(DateTime cutoff)
    {
        using var db = factory.CreateConnection();
        var sql = "DELETE FROM system_status_net_snapshot WHERE Time < @Cutoff";
        await db.ExecuteAsync(sql, new { Cutoff = cutoff });
    }

    private static NetSnapshotPoint ToPoint(SystemStatusNetSnapshot s) => new()
    {
        Time = s.Time,
        Name = s.Name,
        SentBytesPerSec = s.SentBytesPerSec,
        RecvBytesPerSec = s.RecvBytesPerSec,
    };
}
