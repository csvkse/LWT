using LinuxWebTool.Contracts.Features.BackupSync.Contracts;
using LinuxWebTool.Infrastructure.Shared.Persistence;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.BackupSync.Adapters;

/// <summary>
/// 快照批量清理与空间治理引擎，严格内建四重误删防护机制（锁定保护、最新熔断保护、演练预估）。
/// </summary>
public class BackupSnapshotCleaner(BackupSyncStore store, ILogger<BackupSnapshotCleaner> logger)
{
    /// <summary>
    /// 执行或演练批量删除快照
    /// </summary>
    public async Task<BatchDeleteSnapshotsResult> ProcessBatchDeleteAsync(
        BatchDeleteSnapshotsRequest request,
        bool dryRun,
        CancellationToken ct = default)
    {
        var allDbSnapshots = (await store.GetSnapshotsByTaskIdAsync(request.TaskId))
            .OrderByDescending(s => s.CreatedAt)
            .ToList();

        if (allDbSnapshots.Count == 0)
        {
            return new BatchDeleteSnapshotsResult();
        }

        // 1. 甄别候选清理快照
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 手动勾选
        if (request.SnapshotIds.Count > 0)
        {
            foreach (var id in request.SnapshotIds)
            {
                candidates.Add(id);
            }
        }

        // 按早于指定日期规则
        if (request.OlderThan.HasValue)
        {
            foreach (var s in allDbSnapshots.Where(s => s.CreatedAt < request.OlderThan.Value))
            {
                candidates.Add(s.SnapshotId);
            }
        }

        // 按保留最新 N 个规则
        if (request.KeepLatestCount.HasValue && request.KeepLatestCount.Value > 0)
        {
            var keepCount = request.KeepLatestCount.Value;
            var toPrune = allDbSnapshots.Skip(keepCount);
            foreach (var s in toPrune)
            {
                candidates.Add(s.SnapshotId);
            }
        }

        var candidateSnapshots = allDbSnapshots.Where(s => candidates.Contains(s.SnapshotId)).ToList();
        var totalTargeted = candidateSnapshots.Count;

        // 2. 安全防线一：锁定保护 (Pin / Lock Protection)
        var skippedPinned = 0;
        var filteredList = new List<Infrastructure.Shared.Persistence.Entities.BackupSnapshot>();
        foreach (var s in candidateSnapshots)
        {
            if (s.IsPinned && request.SkipPinned)
            {
                skippedPinned++;
                logger.LogInformation("批量清理跳过已加锁快照：{Id}", s.SnapshotId);
            }
            else
            {
                filteredList.Add(s);
            }
        }

        // 3. 安全防线二：熔断底线保护 (Preserve Latest Guard)
        // 无论何种批量清理条件，严禁把整个快照库清空，强制至少保留最新 1 份有效快照
        var newestSnapshot = allDbSnapshots.First();
        var removedLatest = filteredList.RemoveAll(s => s.SnapshotId.Equals(newestSnapshot.SnapshotId, StringComparison.OrdinalIgnoreCase));
        if (removedLatest > 0)
        {
            logger.LogWarning("触发熔断底线保护：强制保留最新快照 {Id}，防止快照库被彻底清空", newestSnapshot.SnapshotId);
        }

        // 4. 空间与清单统计
        var affectedIds = filteredList.Select(s => s.SnapshotId).ToList();
        var estimatedReclaimBytes = filteredList.Sum(s => s.TotalSizeBytes);

        // 5. 若为 DryRun，直接返回预演报告
        if (dryRun)
        {
            return new BatchDeleteSnapshotsResult
            {
                TotalTargeted = totalTargeted,
                DeletedCount = filteredList.Count,
                SkippedPinnedCount = skippedPinned,
                EstimatedReclaimedBytes = estimatedReclaimBytes,
                AffectedSnapshotIds = affectedIds
            };
        }

        // 6. 物理删除执行
        foreach (var s in filteredList)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (Directory.Exists(s.SnapshotPath))
                {
                    Directory.Delete(s.SnapshotPath, recursive: true);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "删除快照物理目录异常：{Path}", s.SnapshotPath);
            }
        }

        // 从数据库批量抹除元数据
        await store.BatchDeleteSnapshotsAsync(request.TaskId, affectedIds);

        logger.LogInformation("批量清理执行完成：任务 {TaskId}，目标 {Targeted}，实际清理 {Deleted}，锁定保护跳过 {Skipped}，释放预估 {Bytes} 字节",
            request.TaskId, totalTargeted, filteredList.Count, skippedPinned, estimatedReclaimBytes);

        return new BatchDeleteSnapshotsResult
        {
            TotalTargeted = totalTargeted,
            DeletedCount = filteredList.Count,
            SkippedPinnedCount = skippedPinned,
            EstimatedReclaimedBytes = estimatedReclaimBytes,
            AffectedSnapshotIds = affectedIds
        };
    }
}
