using LinuxWebTool.Infrastructure.Features.BackupSync.Progress;
using LinuxWebTool.Infrastructure.Features.BackupSync.Retention;
using LinuxWebTool.Infrastructure.Features.BackupSync.Safety;
using Microsoft.Extensions.DependencyInjection;

namespace LinuxWebTool.Infrastructure.Features.BackupSync.Adapters;

public static class BackupSyncExtensions
{
    public static IServiceCollection AddBackupSync(this IServiceCollection services)
    {
        // 核心执行与修剪还原引擎
        services.AddSingleton<HardlinkSnapshotEngine>();
        services.AddSingleton<MirrorSyncEngine>();
        services.AddSingleton<SnapshotRestoreEngine>();
        services.AddSingleton<BackupSnapshotCleaner>();

        // 存储路径健康探测适配器体系（适配器模式）
        services.AddSingleton<ManagedMountPathAdapter>();
        services.AddSingleton<LocalFileSystemPathAdapter>();
        services.AddSingleton<PathStorageAdapterCatalog>();

        // 安全预检策略集合（策略模式）
        services.AddSingleton<IBackupSyncSafetyStrategy, MountHealthGuardStrategy>();
        services.AddSingleton<IBackupSyncSafetyStrategy, DirectoryAccessibilityStrategy>();
        services.AddSingleton<IBackupSyncSafetyStrategy, EmptySourceDisasterPreventionStrategy>();
        services.AddSingleton<IBackupSyncSafetyStrategy, DiskSpaceSafetyStrategy>();

        // 自动清理与修剪保留策略（策略模式）
        services.AddSingleton<IBackupRecordRetentionStrategy, SmartHybridRecordRetentionStrategy>();

        // 安全流水线与状态机/断路器（状态机模式）
        services.AddSingleton<BackupSyncSafetyPipeline>();
        services.AddSingleton<BackupSyncStateMachine>();

        // 全局无锁实时进度追踪与适配器（状态机与观察者模式）
        services.AddSingleton<BackupProgressTracker>();

        // 统一协调器与调度
        services.AddSingleton<BackupSyncCoordinator>();
        services.AddTransient<BackupSyncJob>();
        services.AddSingleton<IBackupSyncScheduler, BackupSyncScheduler>();
        services.AddHostedService<BackupSyncBootstrapService>();
        return services;
    }
}
