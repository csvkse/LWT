using LinuxWebTool.Infrastructure.Features.BackupSync.Adapters;
using LinuxWebTool.Infrastructure.Shared.Persistence;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.BackupSync.Safety;

/// <summary>
/// 备份与同步任务状态机与断路器管理器（状态机模式 + 断路器模式）
/// 掌控任务在 Idle / Running / Success / Failed / AutoPaused 之间的状态迁移
/// </summary>
public class BackupSyncStateMachine(
    BackupSyncStore store,
    IBackupSyncScheduler scheduler,
    IOperationLogger operationLogger,
    ILogger<BackupSyncStateMachine> logger)
{
    private const int MaxConsecutiveFailuresBeforeTrip = 3;

    public const string StatusIdle = "Idle";
    public const string StatusRunning = "Running";
    public const string StatusSuccess = "Success";
    public const string StatusFailed = "Failed";
    public const string StatusAutoPaused = "AutoPaused";

    /// <summary>
    /// 触发自动熔断暂停
    /// </summary>
    public async Task TripAutoPauseAsync(BackupSyncTask task, string reason, string? clientIp = null, bool incrementFailure = true)
    {
        task.LastRunStatus = StatusAutoPaused;
        task.AutoPausedReason = reason;
        task.LastRunMessage = $"[自动暂停保护] {reason}";
        if (incrementFailure)
        {
            task.ConsecutiveFailures++;
        }
        task.LastRunTime = DateTime.Now;

        logger.LogWarning("任务 {Name} 已触发自动暂停熔断保护：{Reason}", task.Name, reason);

        await store.UpdateTaskRunInfoAsync(
            task.Id,
            StatusAutoPaused,
            task.LastRunMessage,
            task.LastRunTime,
            task.NextRunTime,
            task.ConsecutiveFailures,
            task.AutoPausedReason);

        await operationLogger.LogAsync(
            "备份同步自动熔断暂停",
            "备份同步",
            task.Name,
            reason,
            clientIp: clientIp,
            success: false);
    }

    /// <summary>
    /// 记录执行成功并重置断路器计数
    /// </summary>
    public async Task RecordSuccessAsync(BackupSyncTask task, string message, DateTime? nextRun)
    {
        task.LastRunStatus = StatusSuccess;
        task.AutoPausedReason = null;
        task.ConsecutiveFailures = 0;
        task.LastRunMessage = message;
        task.LastRunTime = DateTime.Now;
        task.NextRunTime = nextRun;

        await store.UpdateTaskRunInfoAsync(
            task.Id,
            StatusSuccess,
            message,
            task.LastRunTime,
            nextRun,
            0,
            null);
    }

    /// <summary>
    /// 记录执行失败，并在连续失败达上限时跳闸熔断
    /// </summary>
    public async Task RecordFailureAsync(BackupSyncTask task, string message, DateTime? nextRun, string? clientIp = null)
    {
        task.ConsecutiveFailures++;
        task.LastRunTime = DateTime.Now;

        if (task.ConsecutiveFailures >= MaxConsecutiveFailuresBeforeTrip)
        {
            var tripReason = $"连续失败 {task.ConsecutiveFailures} 次（最近错误：{message}），已触发断路器自动暂停保护";
            await TripAutoPauseAsync(task, tripReason, clientIp, incrementFailure: false);
            return;
        }

        task.LastRunStatus = StatusFailed;
        task.LastRunMessage = message;

        await store.UpdateTaskRunInfoAsync(
            task.Id,
            StatusFailed,
            message,
            task.LastRunTime,
            nextRun,
            task.ConsecutiveFailures,
            null);
    }

    /// <summary>
    /// 手动解除熔断并恢复调度
    /// </summary>
    public async Task<BackupSyncTask> ResumeTaskAsync(Guid taskId, string? clientIp = null)
    {
        var task = await store.GetTaskByIdAsync(taskId)
            ?? throw new InvalidOperationException($"未找到指定任务：{taskId}");

        task.AutoPausedReason = null;
        task.ConsecutiveFailures = 0;
        task.LastRunStatus = StatusIdle;
        task.LastRunMessage = "任务已手动解除熔断并恢复就绪";

        // 重新同步 Quartz 调度器，重新计算 NextRunTime
        task.NextRunTime = await scheduler.SyncAsync(task);

        await store.UpdateTaskRunInfoAsync(
            task.Id,
            StatusIdle,
            task.LastRunMessage,
            task.LastRunTime,
            task.NextRunTime,
            0,
            null);

        logger.LogInformation("任务 {Name} 已解除熔断，恢复调度，下次运行时间：{Next}", task.Name, task.NextRunTime);

        await operationLogger.LogAsync(
            "恢复备份同步任务",
            "备份同步",
            task.Name,
            "已手动解除熔断并恢复定时调度",
            clientIp: clientIp,
            success: true);

        return task;
    }
}
