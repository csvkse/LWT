using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Quartz;
using LinuxWebTool.Contracts.Features.BackupSync.Contracts;
using LinuxWebTool.Contracts.Shared.Contracts;
using LinuxWebTool.Infrastructure.Features.BackupSync.Adapters;
using LinuxWebTool.Infrastructure.Features.BackupSync.Exclusions;
using LinuxWebTool.Infrastructure.Features.BackupSync.Progress;
using LinuxWebTool.Infrastructure.Features.BackupSync.Safety;
using LinuxWebTool.Infrastructure.Features.Scheduling.Adapters;
using LinuxWebTool.Infrastructure.Shared.Persistence;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using LinuxWebTool.WebHost.Composition;
using LinuxWebTool.WebHost.Shared.Extensions;

namespace LinuxWebTool.WebHost.Features.BackupSync.Routes;

/// <summary>
/// 文件夹备份与同步 REST API 控制器。
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class BackupSyncController(
    BackupSyncStore store,
    BackupSyncRecordStore recordStore,
    BackupSyncCoordinator coordinator,
    BackupSnapshotCleaner cleaner,
    IBackupSyncScheduler scheduler,
    BackupSyncStateMachine stateMachine,
    BackupProgressTracker progressTracker,
    ExcludeDetectionPipeline excludePipeline,
    IOperationLogger operationLogger) : MinimalApi.ControllerBase
{
    [HttpGet("Tasks")]
    public async Task<IResult> GetTasks()
    {
        var tasks = await store.GetAllTasksAsync();
        var items = tasks.Select(ToTaskItem).ToList();
        return Ok(items);
    }

    [HttpGet("Tasks/{id:guid}")]
    public async Task<IResult> GetTask(Guid id)
    {
        var task = await store.GetTaskByIdAsync(id);
        if (task is null)
        {
            return NotFound(new MessageResponse("未找到指定任务"));
        }
        return Ok(ToTaskItem(task));
    }

    [HttpGet("Tasks/{id:guid}/Progress")]
    public IResult GetTaskProgress(Guid id)
    {
        var snapshot = progressTracker.GetProgress(id);
        if (snapshot is null)
        {
            return NotFound(new MessageResponse("未找到该任务的实时执行进度"));
        }
        return Results.Json(snapshot, AppJsonSerializerContext.Default.BackupProgressSnapshot);
    }

    [HttpPost("Tasks")]
    public async Task<IResult> CreateTask([FromBody] SaveBackupSyncTaskRequest request)
    {
        var (valid, message, normalizedCron) = ValidateTask(request);
        if (!valid)
        {
            return BadRequest(new MessageResponse(message));
        }

        var entity = new BackupSyncTask
        {
            Name = request.Name.Trim(),
            Mode = (int)request.Mode,
            SourcePath = request.SourcePath.Trim(),
            TargetPath = request.TargetPath.Trim(),
            Enabled = request.Enabled,
            CronExpression = normalizedCron,
            CronDescription = DescribeCron(normalizedCron),
            SyncDirection = (int)request.SyncDirection,
            DeleteHandling = (int)request.DeleteHandling,
            TrashRetentionDays = Math.Max(1, request.TrashRetentionDays),
            KeepLastCount = Math.Max(1, request.KeepLastCount),
            KeepDays = Math.Max(0, request.KeepDays),
            KeepWeeks = Math.Max(0, request.KeepWeeks),
            KeepMonths = Math.Max(0, request.KeepMonths),
            ExcludePatterns = request.ExcludePatterns is { Count: > 0 }
                ? string.Join(";", request.ExcludePatterns.Select(p => p.Trim()).Where(p => !string.IsNullOrEmpty(p)))
                : null
        };

        await store.InsertTaskAsync(entity);
        entity.NextRunTime = await scheduler.SyncAsync(entity);
        await store.UpdateTaskRunInfoAsync(entity.Id, null, null, null, entity.NextRunTime);

        await operationLogger.LogAsync("新建备份同步任务", "备份同步", entity.Name, $"{request.Mode}: {entity.SourcePath} -> {entity.TargetPath}", clientIp: HttpContext.GetClientIp());
        return Ok(ToTaskItem(entity));
    }

    [HttpPut("Tasks/{id:guid}")]
    public async Task<IResult> UpdateTask(Guid id, [FromBody] SaveBackupSyncTaskRequest request)
    {
        var existing = await store.GetTaskByIdAsync(id);
        if (existing is null)
        {
            return NotFound(new MessageResponse("未找到指定任务"));
        }

        var (valid, message, normalizedCron) = ValidateTask(request);
        if (!valid)
        {
            return BadRequest(new MessageResponse(message));
        }

        existing.Name = request.Name.Trim();
        existing.Mode = (int)request.Mode;
        existing.SourcePath = request.SourcePath.Trim();
        existing.TargetPath = request.TargetPath.Trim();
        existing.Enabled = request.Enabled;
        existing.CronExpression = normalizedCron;
        existing.CronDescription = DescribeCron(normalizedCron);
        existing.SyncDirection = (int)request.SyncDirection;
        existing.DeleteHandling = (int)request.DeleteHandling;
        existing.TrashRetentionDays = Math.Max(1, request.TrashRetentionDays);
        existing.KeepLastCount = Math.Max(1, request.KeepLastCount);
        existing.KeepDays = Math.Max(0, request.KeepDays);
        existing.KeepWeeks = Math.Max(0, request.KeepWeeks);
        existing.KeepMonths = Math.Max(0, request.KeepMonths);
        existing.ExcludePatterns = request.ExcludePatterns is { Count: > 0 }
            ? string.Join(";", request.ExcludePatterns.Select(p => p.Trim()).Where(p => !string.IsNullOrEmpty(p)))
            : null;

        await store.UpdateTaskAsync(existing);
        existing.NextRunTime = await scheduler.SyncAsync(existing);
        await store.UpdateTaskRunInfoAsync(existing.Id, existing.LastRunStatus, existing.LastRunMessage, existing.LastRunTime, existing.NextRunTime);

        await operationLogger.LogAsync("更新备份同步任务", "备份同步", existing.Name, $"{request.Mode}: {existing.SourcePath} -> {existing.TargetPath}", clientIp: HttpContext.GetClientIp());
        return Ok(ToTaskItem(existing));
    }

    [HttpDelete("Tasks/{id:guid}")]
    public async Task<IResult> DeleteTask(Guid id)
    {
        var existing = await store.GetTaskByIdAsync(id);
        if (existing is null)
        {
            return NotFound(new MessageResponse("未找到指定任务"));
        }

        await scheduler.RemoveAsync(id);
        await store.DeleteTaskAsync(id);

        await operationLogger.LogAsync("删除备份同步任务", "备份同步", existing.Name, $"TaskId: {id}", clientIp: HttpContext.GetClientIp());
        return Ok(new MessageResponse("任务已成功删除"));
    }

    [HttpPost("Tasks/{id:guid}/Run")]
    public async Task<IResult> RunTask(Guid id)
    {
        var existing = await store.GetTaskByIdAsync(id);
        if (existing is null)
        {
            return NotFound(new MessageResponse("未找到指定任务"));
        }

        var result = await coordinator.RunTaskAsync(id, HttpContext.RequestAborted, isManual: true, clientIp: HttpContext.GetClientIp());
        await operationLogger.LogAsync("立即触发备份同步", "备份同步", existing.Name, result.Message, clientIp: HttpContext.GetClientIp());
        return Ok(result);
    }

    [HttpPost("Tasks/{id:guid}/Resume")]
    public async Task<IResult> ResumeTask(Guid id)
    {
        var existing = await store.GetTaskByIdAsync(id);
        if (existing is null)
        {
            return NotFound(new MessageResponse("未找到指定任务"));
        }

        var resumedTask = await stateMachine.ResumeTaskAsync(id, clientIp: HttpContext.GetClientIp());
        return Ok(ToTaskItem(resumedTask));
    }

    [HttpPost("Tasks/{id:guid}/Cancel")]
    public async Task<IResult> CancelTask(Guid id)
    {
        var existing = await store.GetTaskByIdAsync(id);
        if (existing is null)
        {
            return NotFound(new MessageResponse("未找到指定任务"));
        }

        var cancelled = await coordinator.CancelTaskAsync(id, clientIp: HttpContext.GetClientIp());
        await operationLogger.LogAsync("终止备份同步任务", "备份同步", existing.Name, cancelled ? "已终止或重置任务" : "任务并未在执行中", clientIp: HttpContext.GetClientIp(), success: cancelled);
        return Ok(new MessageResponse(cancelled ? "已成功发送终止信号并释放任务互斥锁" : "任务当前并未在执行中或已处于非运行状态"));
    }

    [HttpGet("Tasks/{id:guid}/Snapshots")]
    public async Task<IResult> GetSnapshots(Guid id)
    {
        var snapshots = (await store.GetSnapshotsByTaskIdAsync(id))
            .Select(BackupSyncCoordinator.ToItem)
            .ToList();
        return Ok(snapshots);
    }

    [HttpGet("Tasks/{id:guid}/Snapshots/{snapshotId}/Files")]
    public async Task<IResult> GetSnapshotFiles(Guid id, string snapshotId, [FromQuery] string path = "")
    {
        var snapshot = await store.GetSnapshotAsync(id, snapshotId);
        if (snapshot is null)
        {
            return NotFound(new MessageResponse("未找到指定快照"));
        }

        var tree = HardlinkSnapshotEngine.BuildSnapshotFileTree(snapshot.SnapshotPath, path);
        return Ok(tree);
    }

    [HttpPost("Tasks/{id:guid}/Snapshots/{snapshotId}/Restore")]
    public async Task<IResult> RestoreSnapshot(Guid id, string snapshotId, [FromBody] RestoreSnapshotRequest request)
    {
        var snapshot = await store.GetSnapshotAsync(id, snapshotId);
        if (snapshot is null)
        {
            return NotFound(new MessageResponse("未找到指定快照"));
        }

        var result = await coordinator.RestoreSnapshotAsync(id, snapshotId, request.TargetRestorePath, request.CleanTargetBeforeRestore, HttpContext.RequestAborted);
        await operationLogger.LogAsync("还原备份快照", "快照恢复", snapshotId, $"还原至 {result.TargetDirectory}，文件数 {result.RestoredFilesCount}", clientIp: HttpContext.GetClientIp());
        return Ok(result);
    }

    [HttpPut("Tasks/{id:guid}/Snapshots/{snapshotId}/Pin")]
    public async Task<IResult> TogglePinSnapshot(Guid id, string snapshotId, [FromBody] TogglePinSnapshotRequest request)
    {
        var snapshot = await store.GetSnapshotAsync(id, snapshotId);
        if (snapshot is null)
        {
            return NotFound(new MessageResponse("未找到指定快照"));
        }

        await store.TogglePinSnapshotAsync(id, snapshotId, request.Pinned, request.Note);
        await operationLogger.LogAsync(request.Pinned ? "锁定保护快照" : "解锁快照", "快照管理", snapshotId, $"TaskId: {id}, Note: {request.Note}", clientIp: HttpContext.GetClientIp());
        return Ok(new MessageResponse(request.Pinned ? "快照已加锁保护" : "快照已解锁"));
    }

    [HttpPost("Snapshots/BatchDeletePreview")]
    public async Task<IResult> BatchDeletePreview([FromBody] BatchDeleteSnapshotsRequest request)
    {
        var previewResult = await cleaner.ProcessBatchDeleteAsync(request, dryRun: true, HttpContext.RequestAborted);
        return Ok(previewResult);
    }

    [HttpPost("Snapshots/BatchDelete")]
    public async Task<IResult> BatchDelete([FromBody] BatchDeleteSnapshotsRequest request)
    {
        var result = await cleaner.ProcessBatchDeleteAsync(request, dryRun: false, HttpContext.RequestAborted);
        await operationLogger.LogAsync("批量删除快照", "快照清理", $"{request.TaskId}", $"清理 {result.DeletedCount} 份，保护跳过 {result.SkippedPinnedCount} 份", clientIp: HttpContext.GetClientIp());
        return Ok(result);
    }

    [HttpGet("Tasks/{id:guid}/PrunePreview")]
    public async Task<IResult> PrunePreview(Guid id)
    {
        var result = await coordinator.PreviewGfsPruneAsync(id);
        return Ok(result);
    }

    [HttpPost("Tasks/{id:guid}/PruneExecute")]
    public async Task<IResult> PruneExecute(Guid id)
    {
        var preview = await coordinator.PreviewGfsPruneAsync(id);
        if (preview.PrunedSnapshots.Count == 0)
        {
            return Ok(new BatchDeleteSnapshotsResult());
        }

        var req = new BatchDeleteSnapshotsRequest
        {
            TaskId = id,
            SnapshotIds = preview.PrunedSnapshots.Select(s => s.SnapshotId).ToList(),
            SkipPinned = true
        };
        var result = await cleaner.ProcessBatchDeleteAsync(req, dryRun: false, HttpContext.RequestAborted);
        await operationLogger.LogAsync("GFS策略批量修剪", "快照修剪", $"{id}", $"修剪 {result.DeletedCount} 份，释放预估 {result.EstimatedReclaimedBytes} 字节", clientIp: HttpContext.GetClientIp());
        return Ok(result);
    }

    [HttpPost("CronPreview")]
    public IResult CronPreview([FromBody] CronPreviewRequest request)
    {
        if (!CronHelper.TryNormalize(request.CronExpression, out var normalized, out var error))
        {
            return Ok(new CronPreviewResponse
            {
                IsValid = false,
                ErrorMessage = error
            });
        }

        try
        {
            var expr = new CronExpression(normalized);
            var results = new List<DateTime>();
            var current = DateTimeOffset.Now;
            var count = Math.Clamp(request.Count, 1, 10);

            for (var i = 0; i < count; i++)
            {
                var next = expr.GetTimeAfter(current);
                if (next is null) break;
                results.Add(next.Value.LocalDateTime);
                current = next.Value;
            }

            return Ok(new CronPreviewResponse
            {
                IsValid = true,
                NormalizedCron = normalized,
                NextRunTimes = results
            });
        }
        catch (Exception ex)
        {
            return Ok(new CronPreviewResponse
            {
                IsValid = false,
                ErrorMessage = ex.Message
            });
        }
    }

    [HttpPost("ProbeExcludes")]
    public IResult ProbeExcludes([FromBody] ProbeExcludesRequest request)
    {
        var response = excludePipeline.ProbePath(request.Path);
        return Ok(response);
    }

    [HttpGet("Records")]
    public async Task<IResult> GetRecords([FromQuery] Guid? taskId, [FromQuery] string? status, [FromQuery] int? page, [FromQuery] int? pageSize)
    {
        var query = new BackupSyncRecordQuery
        {
            TaskId = taskId,
            Status = status,
            Page = page,
            PageSize = pageSize
        };
        var result = await recordStore.QueryRecordsAsync(query);
        return Ok(result);
    }

    [HttpPost("Records/Clear")]
    public async Task<IResult> ClearRecords([FromBody] ClearBackupSyncRecordsRequest request)
    {
        var count = await recordStore.ClearRecordsAsync(request.TaskId, request.OlderThanDays, HttpContext.RequestAborted);
        await operationLogger.LogAsync("清理备份同步记录", "备份同步", request.TaskId?.ToString() ?? "全部任务", $"清理了 {count} 条历史执行记录", clientIp: HttpContext.GetClientIp());
        return Ok(new MessageResponse($"已成功清理 {count} 条执行记录"));
    }

    private static (bool Valid, string Message, string NormalizedCron) ValidateTask(SaveBackupSyncTaskRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return (false, "任务名称不能为空", string.Empty);
        }
        if (string.IsNullOrWhiteSpace(request.SourcePath))
        {
            return (false, "源目录路径不能为空", string.Empty);
        }
        if (string.IsNullOrWhiteSpace(request.TargetPath))
        {
            return (false, "目标目录路径不能为空", string.Empty);
        }
        if (string.Equals(request.SourcePath.Trim(), request.TargetPath.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return (false, "源目录与目标目录不能相同", string.Empty);
        }
        if (!CronHelper.TryNormalize(request.CronExpression, out var normalized, out var err))
        {
            return (false, err, string.Empty);
        }
        return (true, string.Empty, normalized);
    }

    private static string DescribeCron(string cron)
    {
        var parts = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 6)
        {
            if (parts[1] == "0" && parts[2] == "0" && parts[3] == "*" && parts[4] == "*" && parts[5] == "?")
                return "每天 00:00:00";
            if (parts[1] == "0" && parts[3] == "*" && parts[4] == "*" && parts[5] == "?")
                return $"每天 {parts[2].PadLeft(2, '0')}:00";
            if (parts[1].StartsWith("0/") && parts[2] == "*")
                return $"每 {parts[1][2..]} 分钟";
        }
        return cron;
    }

    private static BackupSyncTaskItem ToTaskItem(BackupSyncTask t) => new()
    {
        Id = t.Id,
        Name = t.Name,
        Mode = (TaskMode)t.Mode,
        SourcePath = t.SourcePath,
        TargetPath = t.TargetPath,
        Enabled = t.Enabled,
        CronExpression = t.CronExpression,
        CronDescription = t.CronDescription ?? DescribeCron(t.CronExpression),
        SyncDirection = (SyncDirection)t.SyncDirection,
        DeleteHandling = (DeleteHandling)t.DeleteHandling,
        TrashRetentionDays = t.TrashRetentionDays,
        KeepLastCount = t.KeepLastCount,
        KeepDays = t.KeepDays,
        KeepWeeks = t.KeepWeeks,
        KeepMonths = t.KeepMonths,
        ExcludePatterns = string.IsNullOrWhiteSpace(t.ExcludePatterns)
            ? []
            : t.ExcludePatterns.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList(),
        LastRunTime = t.LastRunTime,
        LastRunStatus = t.LastRunStatus,
        LastRunMessage = t.LastRunMessage,
        NextRunTime = t.NextRunTime,
        ConsecutiveFailures = t.ConsecutiveFailures,
        AutoPausedReason = t.AutoPausedReason,
        CreateTime = t.CreateTime,
        UpdateTime = t.UpdateTime
    };
}
