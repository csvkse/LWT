using Quartz;

namespace LinuxWebTool.Infrastructure.Scheduling;

public static class SchedulingExtensions
{
    /// <summary>注册 Quartz 内存调度、执行 Job 与启动引导服务。</summary>
    public static IServiceCollection AddScheduling(this IServiceCollection services)
    {
        services.AddQuartz(quartz =>
        {
            quartz.SchedulerName = "LinuxWebTool";
        });
        services.AddQuartzHostedService(options => options.WaitForJobsToComplete = false);
        services.AddTransient<ScheduledCommandJob>();
        services.AddSingleton<IScheduleManager, ScheduleManager>();
        services.AddHostedService<ScheduleBootstrapService>();
        return services;
    }
}
