using LinuxWebTool.Contracts.Features.BackupSync.Contracts;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;

namespace LinuxWebTool.Infrastructure.Features.BackupSync.Adapters;

/// <summary>
/// GFS (Grandfather-Father-Son) 确定性多维版本保留与修剪算法引擎。
/// </summary>
public static class GfsPruningEngine
{
    public sealed record RetentionPlan
    {
        public List<BackupSnapshotItem> KeptSnapshots { get; init; } = [];
        public List<BackupSnapshotItem> PrunedSnapshots { get; init; } = [];
        public long EstimatedReclaimableBytes { get; init; }
    }

    /// <summary>
    /// 根据保留参数对快照集合进行确定性分类判定。
    /// </summary>
    public static RetentionPlan EvaluateRetention(
        IEnumerable<BackupSnapshotItem> allSnapshots,
        int keepLastCount,
        int keepDays,
        int keepWeeks,
        int keepMonths)
    {
        var sorted = allSnapshots.OrderByDescending(s => s.CreatedAt).ToList();
        if (sorted.Count == 0)
        {
            return new RetentionPlan();
        }

        var keptSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tagMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 1. 绝对保护：所有被锁定的快照（IsPinned）必须无条件保留
        foreach (var s in sorted.Where(s => s.IsPinned))
        {
            keptSet.Add(s.SnapshotId);
            tagMap[s.SnapshotId] = "Pinned";
        }

        // 2. 熔断底线保护：最新 1 份快照无条件保留
        var newest = sorted[0];
        keptSet.Add(newest.SnapshotId);
        if (!tagMap.ContainsKey(newest.SnapshotId))
        {
            tagMap[newest.SnapshotId] = "Latest";
        }

        // 3. KeepLastCount: 保留最近连续 N 份
        var effectiveLastCount = Math.Max(keepLastCount, 1);
        for (var i = 0; i < Math.Min(effectiveLastCount, sorted.Count); i++)
        {
            var item = sorted[i];
            keptSet.Add(item.SnapshotId);
            if (!tagMap.ContainsKey(item.SnapshotId))
            {
                tagMap[item.SnapshotId] = "Recent";
            }
        }

        // 4. KeepDays: 针对最近 D 天，每天仅保留当天最新（创建时间最晚）的 1 份快照
        if (keepDays > 0)
        {
            var dayBuckets = sorted
                .GroupBy(s => s.CreatedAt.Date)
                .OrderByDescending(g => g.Key)
                .Take(keepDays);

            foreach (var group in dayBuckets)
            {
                var dailyBest = group.First(); // 已经按 CreatedAt 降序排过，First 即当天最新
                keptSet.Add(dailyBest.SnapshotId);
                if (!tagMap.ContainsKey(dailyBest.SnapshotId))
                {
                    tagMap[dailyBest.SnapshotId] = "Daily";
                }
            }
        }

        // 5. KeepWeeks: 针对最近 W 周，每周仅保留该周最新（创建时间最晚）的 1 份快照
        if (keepWeeks > 0)
        {
            // 按 ISO-8601 或年份+周数分组
            var weekBuckets = sorted
                .GroupBy(s => GetYearWeekKey(s.CreatedAt))
                .OrderByDescending(g => g.Key)
                .Take(keepWeeks);

            foreach (var group in weekBuckets)
            {
                var weeklyBest = group.First();
                keptSet.Add(weeklyBest.SnapshotId);
                if (!tagMap.ContainsKey(weeklyBest.SnapshotId))
                {
                    tagMap[weeklyBest.SnapshotId] = "Weekly";
                }
            }
        }

        // 6. KeepMonths: 针对最近 M 个月，每月仅保留该月最新（创建时间最晚）的 1 份快照
        if (keepMonths > 0)
        {
            var monthBuckets = sorted
                .GroupBy(s => $"{s.CreatedAt.Year:D4}-{s.CreatedAt.Month:D2}")
                .OrderByDescending(g => g.Key)
                .Take(keepMonths);

            foreach (var group in monthBuckets)
            {
                var monthlyBest = group.First();
                keptSet.Add(monthlyBest.SnapshotId);
                if (!tagMap.ContainsKey(monthlyBest.SnapshotId))
                {
                    tagMap[monthlyBest.SnapshotId] = "Monthly";
                }
            }
        }

        // 7. 组装结果
        var keptList = new List<BackupSnapshotItem>();
        var prunedList = new List<BackupSnapshotItem>();
        long totalReclaimBytes = 0;

        foreach (var s in sorted)
        {
            if (keptSet.Contains(s.SnapshotId))
            {
                var tag = tagMap.GetValueOrDefault(s.SnapshotId, s.Tag);
                keptList.Add(s with { Tag = tag });
            }
            else
            {
                prunedList.Add(s);
                totalReclaimBytes += s.TotalSizeBytes;
            }
        }

        return new RetentionPlan
        {
            KeptSnapshots = keptList,
            PrunedSnapshots = prunedList,
            EstimatedReclaimableBytes = totalReclaimBytes
        };
    }

    private static string GetYearWeekKey(DateTime dt)
    {
        var cal = System.Globalization.CultureInfo.InvariantCulture.Calendar;
        var week = cal.GetWeekOfYear(dt, System.Globalization.CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
        return $"{dt.Year:D4}-W{week:D2}";
    }
}
