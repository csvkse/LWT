using System.Collections.Concurrent;
using LinuxWebTool.Contracts.Features.BackupSync.Contracts;
using LinuxWebTool.Infrastructure.Shared.Persistence;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.BackupSync.Adapters;

/// <summary>
/// 备份与同步统一任务协调器：负责锁控制、作业派发、引擎调用与生命周期跟踪。
/// </summary>
public class BackupSyncCoordinator(
    BackupSyncStore store,
    HardlinkSnapshotEngine hardlinkEngine,
    MirrorSyncEngine mirrorEngine,
    SnapshotRestoreEngine restoreEngine,
    BackupSnapshotCleaner cleaner,
    ILogger<BackupSyncCoordinator> logger)
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> TaskLocks = new();

    /// <summary>
    /// 触发指定任务执行（备份或直接同步）
    /// </summary>
    public async Task<TaskRunResultResponse> RunTaskAsync(Guid taskId, CancellationToken ct = default)
    {
        var task = await store.GetTaskByIdAsync(taskId);
        if (task is null)
        {
            return new TaskRunResultResponse
            {
                Success = false,
                Message = $"未找到指定任务：{taskId}"
            };
        }

        var taskLock = TaskLocks.GetOrAdd(taskId, _ => new SemaphoreSlim(1, 1));
        if (!await taskLock.WaitAsync(0, ct))
        {
            return new TaskRunResultResponse
            {
                Success = false,
                Message = $"任务 {task.Name} 正在执行中，已跳过本次重复触发"
            };
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await store.UpdateTaskRunInfoAsync(task.Id, "Running", "正在执行中...", DateTime.Now, task.NextRunTime);

            var excludeList = string.IsNullOrWhiteSpace(task.ExcludePatterns)
                ? []
                : task.ExcludePatterns.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            if (task.Mode == (int)TaskMode.Backup)
            {
                // 1. 执行增量快照创建
                var result = await hardlinkEngine.CreateSnapshotAsync(
                    task.SourcePath,
                    task.TargetPath,
                    task.Id,
                    excludeList,
                    ct);

                // 2. 登记元数据到数据库
                var snapshotRecord = new BackupSnapshot
                {
                    SnapshotId = result.SnapshotId,
                    TaskId = task.Id,
                    CreatedAt = result.CreatedAt,
                    TotalFiles = result.TotalFiles,
                    TotalSizeBytes = result.TotalSizeBytes,
                    SnapshotPath = result.SnapshotPath,
                    Tag = "Latest",
                    IsPinned = false
                };
                await store.InsertSnapshotAsync(snapshotRecord);

                // 3. 自动 GFS 轮转修剪过期快照
                try
                {
                    await AutoGfsPruneAsync(task, ct);
                }
                catch (Exception pruneEx)
                {
                    logger.LogWarning(pruneEx, "自动 GFS 修剪异常（不影响本次备份成功）");
                }

                sw.Stop();
                var msg = $"增量备份成功：创建快照 {result.SnapshotId}，包含文件 {result.TotalFiles}，容量 {result.TotalSizeBytes} 字节，耗时 {sw.ElapsedMilliseconds}ms";
                await store.UpdateTaskRunInfoAsync(task.Id, "Success", msg, DateTime.Now, task.NextRunTime);

                return new TaskRunResultResponse
                {
                    Success = true,
                    Message = msg,
                    DurationMs = sw.ElapsedMilliseconds,
                    ProcessedFiles = result.TotalFiles,
                    ProcessedBytes = result.TotalSizeBytes
                };
            }
            else
            {
                // 执行直接同步
                var syncResult = await mirrorEngine.ExecuteSyncAsync(
                    task.SourcePath,
                    task.TargetPath,
                    (SyncDirection)task.SyncDirection,
                    (DeleteHandling)task.DeleteHandling,
                    task.TrashRetentionDays,
                    excludeList,
                    ct);

                sw.Stop();
                var msg = $"同步完成：新增 {syncResult.AddedFiles}，更新 {syncResult.UpdatedFiles}，删除 {syncResult.DeletedFiles}，暂存回收站 {syncResult.TrashedFiles}，耗时 {sw.ElapsedMilliseconds}ms";
                await store.UpdateTaskRunInfoAsync(task.Id, "Success", msg, DateTime.Now, task.NextRunTime);

                return new TaskRunResultResponse
                {
                    Success = true,
                    Message = msg,
                    DurationMs = sw.ElapsedMilliseconds,
                    ProcessedFiles = syncResult.AddedFiles + syncResult.UpdatedFiles,
                    ProcessedBytes = syncResult.TotalBytesTransferred
                };
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            var errMsg = $"任务执行失败：{ex.Message}";
            logger.LogError(ex, "任务执行异常：{Name}", task.Name);
            await store.UpdateTaskRunInfoAsync(task.Id, "Failed", errMsg, DateTime.Now, task.NextRunTime);

            return new TaskRunResultResponse
            {
                Success = false,
                Message = errMsg,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        finally
        {
            taskLock.Release();
        }
    }

    /// <summary>
    /// 自动 GFS 轮转修剪
    /// </summary>
    private async Task AutoGfsPruneAsync(BackupSyncTask task, CancellationToken ct)
    {
        var snapshots = (await store.GetSnapshotsByTaskIdAsync(task.Id))
            .Select(ToItem)
            .ToList();

        var plan = GfsPruningEngine.EvaluateRetention(
            snapshots,
            task.KeepLastCount,
            task.KeepDays,
            task.KeepWeeks,
            task.KeepMonths);

        if (plan.PrunedSnapshots.Count > 0)
        {
            var pruneRequest = new BatchDeleteSnapshotsRequest
            {
                TaskId = task.Id,
                SnapshotIds = plan.PrunedSnapshots.Select(s => s.SnapshotId).ToList(),
                SkipPinned = true
            };
            await cleaner.ProcessBatchDeleteAsync(pruneRequest, dryRun: false, ct);
        }
    }

    /// <summary>
    /// GFS 保留修剪预演
    /// </summary>
    public async Task<PrunePreviewResult> PreviewGfsPruneAsync(Guid taskId)
    {
        var task = await store.GetTaskByIdAsync(taskId);
        if (task is null) return new PrunePreviewResult();

        var snapshots = (await store.GetSnapshotsByTaskIdAsync(taskId))
            .Select(ToItem)
            .ToList();

        var plan = GfsPruningEngine.EvaluateRetention(
            snapshots,
            task.KeepLastCount,
            task.KeepDays,
            task.KeepWeeks,
            task.KeepMonths);

        return new PrunePreviewResult
        {
            KeptSnapshots = plan.KeptSnapshots,
            PrunedSnapshots = plan.PrunedSnapshots,
            ReclaimableSizeBytes = plan.EstimatedReclaimableBytes
        };
    }

    /// <summary>
    /// 执行快照还原
    /// </summary>
    public async Task<RestoreSnapshotResult> RestoreSnapshotAsync(
        Guid taskId,
        string snapshotId,
        string? targetRestorePath,
        bool cleanTargetBeforeRestore,
        CancellationToken ct = default)
    {
        var task = await store.GetTaskByIdAsync(taskId);
        if (task is null) throw new InvalidOperationException($"未找到备份任务：{taskId}");

        var snapshot = await store.GetSnapshotAsync(taskId, snapshotId);
        if (snapshot is null) throw new InvalidOperationException($"未找到快照：{snapshotId}");

        var restoreDest = string.IsNullOrWhiteSpace(targetRestorePath)
            ? task.SourcePath
            : targetRestorePath.Trim();

        return await restoreEngine.RestoreAsync(snapshot.SnapshotPath, restoreDest, cleanTargetBeforeRestore, ct);
    }

    public static BackupSnapshotItem ToItem(BackupSnapshot s) => new()
    {
        SnapshotId = s.SnapshotId,
        TaskId = s.TaskId,
        CreatedAt = s.CreatedAt,
        TotalFiles = s.TotalFiles,
        TotalSizeBytes = s.TotalSizeBytes,
        SnapshotPath = s.SnapshotPath,
        Tag = s.Tag,
        IsPinned = s.IsPinned,
        Note = s.Note
    };
}
