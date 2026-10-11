using LinuxWebTool.Infrastructure.Shared.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.BackupSync.Adapters;

/// <summary>
/// 应用启动时将所有启用中的备份与同步任务注册进 Quartz 调度器。
/// </summary>
public sealed class BackupSyncBootstrapService(
    BackupSyncStore store,
    IBackupSyncScheduler scheduler,
    ILogger<BackupSyncBootstrapService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var tasks = (await store.GetEnabledTasksAsync()).ToList();
            await scheduler.SyncAllAsync(tasks);
            logger.LogInformation("备份与同步调度引导完成：已注册 {Count} 个启用任务", tasks.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "备份与同步调度引导失败");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
