using Quartz;

namespace LinuxWebTool.Infrastructure.Scheduling;

/// <summary>Quartz 内存调度同步实现：任务 JobKey=任务 Id，组 LinuxCommand；一次性触发走 LinuxCommandOnce 组。</summary>
public sealed class ScheduleManager(ISchedulerFactory schedulerFactory, ILogger<ScheduleManager> logger) : IScheduleManager
{
    private const string GroupName = "LinuxCommand";
    private const string OnceGroupName = "LinuxCommandOnce";

    public async Task<DateTime?> SyncAsync(ScheduleTask task)
    {
        var scheduler = await schedulerFactory.GetScheduler();
        var key = new JobKey(task.Id.ToString(), GroupName);
        await scheduler.DeleteJob(key);

        if (!task.Enabled)
        {
            logger.LogInformation("定时任务已停用并移除调度：{Task}", task.Name);
            return null;
        }

        if (!CronExpression.IsValidExpression(task.CronExpression))
        {
            throw new InvalidOperationException($"无效的 Cron 表达式：{task.CronExpression}");
        }

        var job = JobBuilder.Create<ScheduledCommandJob>()
            .WithIdentity(key)
            .WithDescription(task.Name)
            .UsingJobData(ScheduledCommandJob.TaskIdKey, task.Id.ToString())
            .Build();

        var trigger = TriggerBuilder.Create()
            .ForJob(key)
            .WithIdentity($"{task.Id}-trigger", GroupName)
            .WithCronSchedule(task.CronExpression, schedule => schedule.WithMisfireHandlingInstructionDoNothing())
            .Build();

        await scheduler.ScheduleJob(job, trigger);

        var nextRun = trigger.GetFireTimeAfter(DateTimeOffset.Now)?.LocalDateTime;
        logger.LogInformation("定时任务已同步：{Task}（{Cron}），下次执行 {Next:yyyy-MM-dd HH:mm:ss}", task.Name, task.CronExpression, nextRun);
        return nextRun;
    }

    public async Task RemoveAsync(Guid taskId)
    {
        var scheduler = await schedulerFactory.GetScheduler();
        await scheduler.DeleteJob(new JobKey(taskId.ToString(), GroupName));
    }

    public async Task TriggerNowAsync(ScheduleTask task)
    {
        var scheduler = await schedulerFactory.GetScheduler();
        var key = new JobKey($"now-{task.Id:N}", OnceGroupName);

        var job = JobBuilder.Create<ScheduledCommandJob>()
            .WithIdentity(key)
            .WithDescription($"{task.Name}（手动触发）")
            .UsingJobData(ScheduledCommandJob.TaskIdKey, task.Id.ToString())
            .Build();

        var trigger = TriggerBuilder.Create()
            .ForJob(key)
            .WithIdentity($"now-{task.Id:N}", OnceGroupName)
            .StartNow()
            .Build();

        // 非 durable 的 Job 在触发完成后由调度器自动清理。
        await scheduler.ScheduleJob(job, trigger);
        logger.LogInformation("定时任务手动触发：{Task}", task.Name);
    }

    public async Task SyncAllAsync(IReadOnlyList<ScheduleTask> tasks)
    {
        foreach (var task in tasks)
        {
            try
            {
                await SyncAsync(task);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "定时任务调度重建失败：{Task}（{Cron}）", task.Name, task.CronExpression);
            }
        }
    }
}
