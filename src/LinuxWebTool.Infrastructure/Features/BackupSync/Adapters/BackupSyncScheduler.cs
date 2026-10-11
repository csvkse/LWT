using Quartz;
using LinuxWebTool.Infrastructure.Features.Scheduling.Adapters;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.BackupSync.Adapters;

public interface IBackupSyncScheduler
{
    Task<DateTime?> SyncAsync(BackupSyncTask task);
    Task RemoveAsync(Guid taskId);
    Task TriggerNowAsync(BackupSyncTask task);
    Task SyncAllAsync(IEnumerable<BackupSyncTask> tasks);
}

public class BackupSyncScheduler(ISchedulerFactory schedulerFactory, ILogger<BackupSyncScheduler> logger) : IBackupSyncScheduler
{
    private const string GroupName = "BackupSync";
    private const string OnceGroupName = "BackupSyncOnce";

    public async Task<DateTime?> SyncAsync(BackupSyncTask task)
    {
        var scheduler = await schedulerFactory.GetScheduler();
        var key = new JobKey(task.Id.ToString(), GroupName);
        await scheduler.DeleteJob(key);

        if (!task.Enabled)
        {
            logger.LogInformation("备份同步任务已停用并移除调度：{Task}", task.Name);
            return null;
        }

        if (!CronHelper.TryNormalize(task.CronExpression, out var normalized, out var err))
        {
            throw new InvalidOperationException($"无效的 Cron 表达式：{task.CronExpression}，{err}");
        }

        if (!CronExpression.IsValidExpression(normalized))
        {
            throw new InvalidOperationException($"无效的 Quartz Cron 表达式：{normalized}");
        }

        var job = JobBuilder.Create<BackupSyncJob>()
            .WithIdentity(key)
            .WithDescription(task.Name)
            .UsingJobData(BackupSyncJob.TaskIdKey, task.Id.ToString())
            .Build();

        var trigger = TriggerBuilder.Create()
            .ForJob(key)
            .WithIdentity($"{task.Id}-trigger", GroupName)
            .WithCronSchedule(normalized, schedule => schedule.WithMisfireHandlingInstructionDoNothing())
            .Build();

        await scheduler.ScheduleJob(job, trigger);

        var nextRun = trigger.GetFireTimeAfter(DateTimeOffset.Now)?.LocalDateTime;
        logger.LogInformation("备份同步任务已调度：{Task}（{Cron}），下次执行 {Next:yyyy-MM-dd HH:mm:ss}", task.Name, normalized, nextRun);
        return nextRun;
    }

    public async Task RemoveAsync(Guid taskId)
    {
        var scheduler = await schedulerFactory.GetScheduler();
        await scheduler.DeleteJob(new JobKey(taskId.ToString(), GroupName));
    }

    public async Task TriggerNowAsync(BackupSyncTask task)
    {
        var scheduler = await schedulerFactory.GetScheduler();
        var key = new JobKey($"now-{task.Id:N}", OnceGroupName);

        var job = JobBuilder.Create<BackupSyncJob>()
            .WithIdentity(key)
            .WithDescription($"{task.Name}（手动触发）")
            .UsingJobData(BackupSyncJob.TaskIdKey, task.Id.ToString())
            .Build();

        var trigger = TriggerBuilder.Create()
            .ForJob(key)
            .WithIdentity($"now-trigger-{task.Id:N}", OnceGroupName)
            .StartNow()
            .Build();

        await scheduler.ScheduleJob(job, trigger);
        logger.LogInformation("已触发立即执行备份同步任务：{Task}", task.Name);
    }

    public async Task SyncAllAsync(IEnumerable<BackupSyncTask> tasks)
    {
        foreach (var task in tasks)
        {
            try
            {
                await SyncAsync(task);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "恢复任务调度失败：{Task}（{Id}）", task.Name, task.Id);
            }
        }
    }
}
