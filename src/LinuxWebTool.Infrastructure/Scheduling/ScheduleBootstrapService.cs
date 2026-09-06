namespace LinuxWebTool.Infrastructure.Scheduling;

/// <summary>应用启动时把所有启用中的定时任务重建进 Quartz 内存调度。</summary>
public sealed class ScheduleBootstrapService(ScheduleStore scheduleStore, IScheduleManager scheduleManager, ILogger<ScheduleBootstrapService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var tasks = await scheduleStore.GetEnabledAsync();
            await scheduleManager.SyncAllAsync(tasks);
            logger.LogInformation("定时任务引导完成：已注册 {Count} 个启用任务", tasks.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "定时任务引导失败");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
