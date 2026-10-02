using LinuxWebTool.Contracts.Models;
using LinuxWebTool.Infrastructure.Persistence;
using LinuxWebTool.Infrastructure.SystemInfo;

namespace LinuxWebTool.Infrastructure.Mount;

public sealed class WebDavMountStartupService(
    WebDavMountStore store,
    WebDavMountService operations,
    MountOperationCoordinator coordinator,
    ILogger<WebDavMountStartupService> logger) : BackgroundService
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task WaitReadyAsync(CancellationToken cancellationToken) => _ready.Task.WaitAsync(cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(2000, stoppingToken);
            var mounts = (await store.GetAllAsync()).Where(m => m.Enabled).ToArray();
            foreach (var mount in mounts)
                SystemStatusProvider.ManagedMountPoints[MountOperationCoordinator.NormalizePath(mount.LocalPath)] = 0;
            _ready.TrySetResult();

            if (!WebDavMountService.IsSupported) return;

            foreach (var mount in mounts.Where(m => m.AutoMount))
            {
                if (stoppingToken.IsCancellationRequested) return;
                if (operations.GetStatus(mount) == SmbMountStatus.Mounted) continue;
                coordinator.BeginStartup(mount.LocalPath);
                try
                {
                    for (var attempt = 1; attempt <= 3; attempt++)
                    {
                        if (attempt > 1) await Task.Delay(attempt == 2 ? 5000 : 15000, stoppingToken);
                        var result = await coordinator.RunWithMountLockAsync(mount.LocalPath,
                            () => operations.MountAsync(mount, stoppingToken), stoppingToken);
                        if (result.Success) break;
                        logger.LogWarning("WebDAV 启动挂载失败（{Attempt}/3）：{Name}，{Error}", attempt, mount.Name, result.Message);
                    }
                }
                finally { coordinator.EndStartup(mount.LocalPath); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogError(ex, "WebDAV 启动挂载失败"); }
        finally { _ready.TrySetResult(); }
    }
}
