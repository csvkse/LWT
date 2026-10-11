using Microsoft.Extensions.DependencyInjection;

namespace LinuxWebTool.Infrastructure.Features.BackupSync.Adapters;

public static class BackupSyncExtensions
{
    public static IServiceCollection AddBackupSync(this IServiceCollection services)
    {
        services.AddSingleton<HardlinkSnapshotEngine>();
        services.AddSingleton<MirrorSyncEngine>();
        services.AddSingleton<SnapshotRestoreEngine>();
        services.AddSingleton<BackupSnapshotCleaner>();
        services.AddSingleton<BackupSyncCoordinator>();
        services.AddTransient<BackupSyncJob>();
        services.AddSingleton<IBackupSyncScheduler, BackupSyncScheduler>();
        services.AddHostedService<BackupSyncBootstrapService>();
        return services;
    }
}
