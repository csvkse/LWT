using System.Collections.Concurrent;
using LinuxWebTool.Contracts.Features.BackupSync.Contracts;
using LinuxWebTool.Infrastructure.Features.BackupSync.Progress;
using LinuxWebTool.Infrastructure.Features.BackupSync.Safety;
using LinuxWebTool.Infrastructure.Shared.Persistence;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.BackupSync.Adapters;

/// <summary>
/// 备份与同步统一任务协调器：负责锁控制、作业派发、安全预检、引擎调用与生命周期跟踪。
/// </summary>
public class BackupSyncCoordinator(
    BackupSyncStore store,
    BackupSyncRecordStore recordStore,
    HardlinkSnapshotEngine hardlinkEngine,
    MirrorSyncEngine mirrorEngine,
    SnapshotRestoreEngine restoreEngine,
    BackupSnapshotCleaner cleaner,
    BackupSyncSafetyPipeline safetyPipeline,
    BackupSyncStateMachine stateMachine,
    BackupProgressTracker progressTracker,
    ILogger<BackupSyncCoordinator> logger)
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> TaskLocks = new();
    private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> ActiveCts = new();

    /// <summary>
    /// 触发指定任务执行（备份或直接同步）
    /// </summary>
    public async Task<TaskRunResultResponse> RunTaskAsync(
        Guid taskId,
        CancellationToken ct = default,
        bool isManual = false,
        string? clientIp = null)
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

        // 1. 检查断路器状态：若已处于自动暂停熔断状态且由定时调度触发，自动跳过执行
        if (task.LastRunStatus == BackupSyncStateMachine.StatusAutoPaused && !isManual)
        {
            logger.LogWarning("任务 {Name} 已处于自动暂停熔断状态，已跳过定时调度：{Reason}", task.Name, task.AutoPausedReason);
            return new TaskRunResultResponse
            {
                Success = false,
                Message = $"任务 {task.Name} 已处于自动暂停熔断状态，已跳过定时调度：{task.AutoPausedReason}"
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

        var recordId = await stateMachine.StartExecutionRecordAsync(task, isManual, clientIp);
        var reporter = progressTracker.StartTracking(task.Id, task.Name, (TaskMode)task.Mode);
        reporter.UpdatePhase(BackupExecutionPhase.PreflightChecking, "正在执行安全预检与连通性验证...");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromHours(4)); // 4 小时绝对硬超时看门狗，防挂死
        ActiveCts[taskId] = linkedCts;

        using var checkpointCts = new CancellationTokenSource();
        var checkpointTask = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            while (!checkpointCts.IsCancellationRequested && await timer.WaitForNextTickAsync(checkpointCts.Token).ConfigureAwait(false))
            {
                try
                {
                    var snap = reporter.GetSnapshot();
                    if (snap.IsRunning)
                    {
                        var summary = $"{snap.PhaseDescription} ({snap.ProcessedFiles}/{Math.Max(snap.ProcessedFiles, snap.ScannedFiles)})";
                        await recordStore.UpdateProgressCheckpointAsync(
                            recordId,
                            snap.ElapsedMs,
                            snap.ProcessedFiles,
                            snap.ProcessedBytes,
                            summary).ConfigureAwait(false);
                    }
                }
                catch { }
            }
        }, CancellationToken.None);

        try
        {
            // 2. 真实动作前执行安全预检流水线（挂载状态机检测、目录可读写 Canary 探针、灾难空源防误删、磁盘空间防护）
            var preflight = await safetyPipeline.EvaluateAsync(task, linkedCts.Token);
            if (!preflight.IsPassed)
            {
                checkpointCts.Cancel();
                try { await checkpointTask.ConfigureAwait(false); } catch { }
                sw.Stop();
                var preflightMsg = preflight.ErrorMessage ?? "安全策略预检未通过";
                if (preflight.IsFatal)
                {
                    reporter.UpdatePhase(BackupExecutionPhase.AutoPaused, $"[安全熔断] {preflightMsg}");
                    progressTracker.CompleteTracking(task.Id, false, preflightMsg);
                    await stateMachine.TripAutoPauseAsync(task, preflightMsg, clientIp);
                    await stateMachine.CompleteExecutionRecordAsync(
                        recordId, task, BackupSyncStateMachine.StatusAutoPaused,
                        sw.ElapsedMilliseconds, 0, 0, 0, 0, 0, 0,
                        $"[安全熔断] {preflightMsg}", preflightMsg);
                    return new TaskRunResultResponse
                    {
                        Success = false,
                        Message = $"[安全熔断] 任务已自动暂停：{preflightMsg}",
                        DurationMs = sw.ElapsedMilliseconds
                    };
                }
                else
                {
                    reporter.UpdatePhase(BackupExecutionPhase.Faulted, preflightMsg);
                    progressTracker.CompleteTracking(task.Id, false, preflightMsg);
                    await stateMachine.RecordFailureAsync(task, preflightMsg, task.NextRunTime, clientIp);
                    await stateMachine.CompleteExecutionRecordAsync(
                        recordId, task, BackupSyncStateMachine.StatusFailed,
                        sw.ElapsedMilliseconds, 0, 0, 0, 0, 0, 0,
                        preflightMsg, preflightMsg);
                    return new TaskRunResultResponse
                    {
                        Success = false,
                        Message = preflightMsg,
                        DurationMs = sw.ElapsedMilliseconds
                    };
                }
            }

            await store.UpdateTaskRunInfoAsync(
                task.Id,
                BackupSyncStateMachine.StatusRunning,
                "正在执行中...",
                DateTime.Now,
                task.NextRunTime,
                task.ConsecutiveFailures,
                task.AutoPausedReason);

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
                    reporter,
                    linkedCts.Token);

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
                    reporter.UpdatePhase(BackupExecutionPhase.RetentionPruning, "正在修剪过期快照与提交元数据...");
                    await AutoGfsPruneAsync(task, linkedCts.Token);
                }
                catch (Exception pruneEx)
                {
                    logger.LogWarning(pruneEx, "自动 GFS 修剪异常（不影响本次备份成功）");
                }

                checkpointCts.Cancel();
                try { await checkpointTask.ConfigureAwait(false); } catch { }
                sw.Stop();
                var msg = $"增量备份成功：创建快照 {result.SnapshotId}，包含文件 {result.TotalFiles}，容量 {result.TotalSizeBytes} 字节，跳过异常 {result.SkippedFiles}，耗时 {sw.ElapsedMilliseconds}ms";
                reporter.UpdatePhase(BackupExecutionPhase.Completed, "增量备份已顺利完成");
                progressTracker.CompleteTracking(task.Id, true);
                await stateMachine.RecordSuccessAsync(task, msg, task.NextRunTime);
                await stateMachine.CompleteExecutionRecordAsync(
                    recordId, task, BackupSyncStateMachine.StatusSuccess,
                    sw.ElapsedMilliseconds,
                    result.TotalFiles, result.TotalSizeBytes,
                    result.CopiedFiles, 0, 0, result.SkippedFiles,
                    msg, null);

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
                    reporter,
                    linkedCts.Token);

                checkpointCts.Cancel();
                try { await checkpointTask.ConfigureAwait(false); } catch { }
                sw.Stop();
                var msg = $"同步完成：新增 {syncResult.AddedFiles}，更新 {syncResult.UpdatedFiles}，删除 {syncResult.DeletedFiles}，暂存回收站 {syncResult.TrashedFiles}，跳过异常 {syncResult.SkippedFiles}，耗时 {sw.ElapsedMilliseconds}ms";
                reporter.UpdatePhase(BackupExecutionPhase.Completed, "目录镜像同步已顺利完成");
                progressTracker.CompleteTracking(task.Id, true);
                await stateMachine.RecordSuccessAsync(task, msg, task.NextRunTime);
                await stateMachine.CompleteExecutionRecordAsync(
                    recordId, task, BackupSyncStateMachine.StatusSuccess,
                    sw.ElapsedMilliseconds,
                    syncResult.AddedFiles + syncResult.UpdatedFiles, syncResult.TotalBytesTransferred,
                    syncResult.AddedFiles, syncResult.UpdatedFiles, syncResult.DeletedFiles, syncResult.SkippedFiles,
                    msg, null);

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
        catch (OperationCanceledException)
        {
            checkpointCts.Cancel();
            try { await checkpointTask.ConfigureAwait(false); } catch { }
            sw.Stop();
            var cancelMsg = linkedCts.IsCancellationRequested ? "任务已被主动终止或执行超时" : "任务已中断";
            logger.LogWarning("任务已被取消：{Name}，耗时 {Elapsed}ms", task.Name, sw.ElapsedMilliseconds);
            reporter.UpdatePhase(BackupExecutionPhase.Faulted, cancelMsg);
            progressTracker.CompleteTracking(task.Id, false, cancelMsg);
            await stateMachine.RecordCancelledAsync(task, cancelMsg, clientIp);
            var snap = progressTracker.GetProgress(task.Id);
            var processedFiles = snap?.ProcessedFiles ?? 0;
            var processedBytes = snap?.ProcessedBytes ?? 0;
            var skippedFiles = snap?.SkippedFiles ?? 0;
            await stateMachine.CompleteExecutionRecordAsync(
                recordId, task, BackupSyncStateMachine.StatusCancelled,
                sw.ElapsedMilliseconds,
                processedFiles, processedBytes,
                0, 0, 0, skippedFiles,
                cancelMsg, null);

            return new TaskRunResultResponse
            {
                Success = false,
                Message = cancelMsg,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            checkpointCts.Cancel();
            try { await checkpointTask.ConfigureAwait(false); } catch { }
            sw.Stop();
            var errMsg = $"任务执行失败：{ex.Message}";
            logger.LogError(ex, "任务执行异常：{Name}", task.Name);
            reporter.UpdatePhase(BackupExecutionPhase.Faulted, errMsg);
            progressTracker.CompleteTracking(task.Id, false, errMsg);
            await stateMachine.RecordFailureAsync(task, errMsg, task.NextRunTime, clientIp);
            await stateMachine.CompleteExecutionRecordAsync(
                recordId, task, BackupSyncStateMachine.StatusFailed,
                sw.ElapsedMilliseconds,
                0, 0, 0, 0, 0, 0,
                errMsg, ex.ToString());

            return new TaskRunResultResponse
            {
                Success = false,
                Message = errMsg,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        finally
        {
            ActiveCts.TryRemove(taskId, out _);
            taskLock.Release();
        }
    }

    /// <summary>
    /// 手动终止或重置指定任务的执行状态并释放互斥锁
    /// </summary>
    public async Task<bool> CancelTaskAsync(Guid taskId, string? reason = null, string? clientIp = null)
    {
        var hasActive = false;
        if (ActiveCts.TryGetValue(taskId, out var cts))
        {
            hasActive = true;
            logger.LogInformation("正在手动取消活跃任务 {TaskId}：{Reason}", taskId, reason ?? "用户主动终止");
            try
            {
                cts.Cancel();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "通知取消令牌异常：{TaskId}", taskId);
            }
        }

        // 无论内存中是否仍有活跃 CTS，检查数据库是否有残留 Running 状态，进行自愈
        var task = await store.GetTaskByIdAsync(taskId);
        if (task is not null && task.LastRunStatus == BackupSyncStateMachine.StatusRunning)
        {
            await stateMachine.RecordCancelledAsync(task, reason ?? "用户手动终止（重置运行状态）", clientIp);
            var runningRecords = await recordStore.GetRunningRecordsByTaskIdAsync(taskId);
            foreach (var rec in runningRecords)
            {
                var duration = Math.Max(0, (long)(DateTime.Now - rec.StartTime).TotalMilliseconds);
                await stateMachine.CompleteExecutionRecordAsync(
                    rec.Id, task, BackupSyncStateMachine.StatusCancelled,
                    duration,
                    rec.ProcessedFiles, rec.ProcessedBytes, 0, 0, 0, 0,
                    "用户手动重置终止", null);
            }
            hasActive = true;
        }

        return hasActive;
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
