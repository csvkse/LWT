using Xunit;

namespace LinuxWebTool.UnitTests;

public class GfsRetentionPruningTests
{
    private static BackupSnapshotItem CreateSnapshot(string id, DateTime dt, long size = 1024, bool pinned = false) => new()
    {
        SnapshotId = id,
        TaskId = Guid.NewGuid(),
        CreatedAt = dt,
        TotalFiles = 10,
        TotalSizeBytes = size,
        SnapshotPath = $"/backup/{id}",
        Tag = string.Empty,
        IsPinned = pinned
    };

    [Fact]
    public void Gfs_空快照集返回空计划()
    {
        var plan = GfsPruningEngine.EvaluateRetention([], 5, 7, 4, 12);
        Assert.Empty(plan.KeptSnapshots);
        Assert.Empty(plan.PrunedSnapshots);
        Assert.Equal(0, plan.EstimatedReclaimableBytes);
    }

    [Fact]
    public void Gfs_熔断底线保护_无论保留参数多苛刻至少保留最新一份快照()
    {
        var now = DateTime.Now;
        var snapshots = new List<BackupSnapshotItem>
        {
            CreateSnapshot("snap-1", now.AddDays(-10), 500),
            CreateSnapshot("snap-2", now.AddDays(-5), 600),
            CreateSnapshot("snap-3", now, 700)
        };

        // 所有保留均为 0
        var plan = GfsPruningEngine.EvaluateRetention(snapshots, keepLastCount: 0, keepDays: 0, keepWeeks: 0, keepMonths: 0);

        Assert.Single(plan.KeptSnapshots);
        Assert.Equal("snap-3", plan.KeptSnapshots[0].SnapshotId);
        Assert.Equal("Latest", plan.KeptSnapshots[0].Tag);
        Assert.Equal(2, plan.PrunedSnapshots.Count);
        Assert.Equal(1100, plan.EstimatedReclaimableBytes);
    }

    [Fact]
    public void Gfs_锁定快照保护_被打上IsPinned的快照绝对不被修剪()
    {
        var now = DateTime.Now;
        var snapshots = new List<BackupSnapshotItem>
        {
            CreateSnapshot("snap-old-pinned", now.AddDays(-300), 2000, pinned: true),
            CreateSnapshot("snap-old-normal", now.AddDays(-200), 1000, pinned: false),
            CreateSnapshot("snap-newest", now, 1500, pinned: false)
        };

        var plan = GfsPruningEngine.EvaluateRetention(snapshots, keepLastCount: 1, keepDays: 0, keepWeeks: 0, keepMonths: 0);

        var keptIds = plan.KeptSnapshots.Select(s => s.SnapshotId).ToHashSet();
        Assert.Contains("snap-old-pinned", keptIds);
        Assert.Contains("snap-newest", keptIds);
        Assert.DoesNotContain("snap-old-normal", keptIds);
        Assert.Single(plan.PrunedSnapshots);
        Assert.Equal("snap-old-normal", plan.PrunedSnapshots[0].SnapshotId);
    }

    [Fact]
    public void Gfs_KeepLastCount_优先保留连续最新N个快照()
    {
        var baseTime = new DateTime(2026, 10, 1, 12, 0, 0);
        var snapshots = Enumerable.Range(1, 10)
            .Select(i => CreateSnapshot($"snap-{i:D2}", baseTime.AddHours(i), 100))
            .ToList();

        var plan = GfsPruningEngine.EvaluateRetention(snapshots, keepLastCount: 4, keepDays: 0, keepWeeks: 0, keepMonths: 0);

        Assert.Equal(4, plan.KeptSnapshots.Count);
        Assert.Equal(6, plan.PrunedSnapshots.Count);
        var keptIds = plan.KeptSnapshots.Select(s => s.SnapshotId).ToList();
        Assert.Equal(["snap-10", "snap-09", "snap-08", "snap-07"], keptIds);
    }

    [Fact]
    public void Gfs_KeepDays_在同一天内只保留该天最晚的一份快照()
    {
        var day1Morning = new DateTime(2026, 10, 10, 8, 0, 0);
        var day1Noon = new DateTime(2026, 10, 10, 12, 0, 0);
        var day1Night = new DateTime(2026, 10, 10, 23, 0, 0);

        var day2Morning = new DateTime(2026, 10, 11, 9, 0, 0);
        var day2Night = new DateTime(2026, 10, 11, 22, 0, 0);

        var snapshots = new List<BackupSnapshotItem>
        {
            CreateSnapshot("d1-morn", day1Morning),
            CreateSnapshot("d1-noon", day1Noon),
            CreateSnapshot("d1-night", day1Night),
            CreateSnapshot("d2-morn", day2Morning),
            CreateSnapshot("d2-night", day2Night)
        };

        // 保留 2 天每日快照，连续数量设为 1
        var plan = GfsPruningEngine.EvaluateRetention(snapshots, keepLastCount: 1, keepDays: 2, keepWeeks: 0, keepMonths: 0);

        var keptIds = plan.KeptSnapshots.Select(s => s.SnapshotId).ToHashSet();
        Assert.Contains("d2-night", keptIds);
        Assert.Contains("d1-night", keptIds);
        Assert.DoesNotContain("d1-morn", keptIds);
        Assert.DoesNotContain("d1-noon", keptIds);
        Assert.DoesNotContain("d2-morn", keptIds);
    }
}
