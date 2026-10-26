using Quartz;
using LinuxWebTool.Infrastructure.Features.BackupSync.Safety;
using LinuxWebTool.Infrastructure.Shared.Persistence;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.BackupSync.Adapters;

/// <summary>
/// Quartz 备份与同步调度作业。
/// </summary>
[DisallowConcurrentExecution]
public class BackupSyncJob(
    BackupSyncCoordinator coordinator,
    BackupSyncStore store,
    ILogger<BackupSyncJob> logger) : IJob
{
    public const string TaskIdKey = "taskId";

    public async Task Execute(IJobExecutionContext context)
    {
        var dataMap = context.MergedJobDataMap;
        if (!Guid.TryParse(dataMap.GetString(TaskIdKey), out var taskId))
        {
            logger.LogError("备份同步作业缺少有效的 taskId 参数：{JobKey}", context.JobDetail.Key);
            return;
        }

        var task = await store.GetTaskByIdAsync(taskId);
        if (task is null)
        {
            logger.LogWarning("备份同步任务不存在，跳过执行：{TaskId}", taskId);
            return;
        }

        var nextRun = context.NextFireTimeUtc?.LocalDateTime;

        // 若任务已被断路器熔断自动暂停，直接跳过本次执行，防止持续对异常挂载施压
        if (task.LastRunStatus == BackupSyncStateMachine.StatusAutoPaused)
        {
            logger.LogWarning("备份同步任务 {Name} 处于自动暂停熔断状态，跳过本次调度执行：{Reason}", task.Name, task.AutoPausedReason);
            await store.UpdateTaskNextRunTimeAsync(task.Id, nextRun);
            return;
        }

        logger.LogInformation("Quartz 触发备份同步任务：{Name}（{Id}）", task.Name, task.Id);
        var result = await coordinator.RunTaskAsync(taskId, context.CancellationToken, isManual: false);

        await store.UpdateTaskNextRunTimeAsync(task.Id, nextRun);
    }
}
