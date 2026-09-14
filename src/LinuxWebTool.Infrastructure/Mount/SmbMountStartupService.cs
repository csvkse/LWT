using LinuxWebTool.Contracts.Models;
using LinuxWebTool.Infrastructure.Persistence;
using LinuxWebTool.Infrastructure.SystemInfo;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace LinuxWebTool.Infrastructure.Mount;

/// <summary>
/// 应用启动时重放 SMB 挂载（不写 /etc/fstab，避免破坏宿主机引导配置）：
/// 1. 全部启用条目注册状态页白名单 + 确保凭据文件存在；
/// 2. AutoMount 条目未挂载时自动重挂（覆盖容器重启丢 mount namespace 的场景）。
/// </summary>
public sealed class SmbMountStartupService(
    SmbMountStore store,
    SmbMountService mountService,
    MountOperationCoordinator coordinator,
    ILogger<SmbMountStartupService> logger) : BackgroundService
{
    private const int MaxRetries = 10;
    private const int MountAttemptTimeoutSeconds = 45;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 等待数据库 / 网络就绪，避开启动尖峰
        try
        {
            await Task.Delay(2000, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        List<SmbMount> mounts;
        try
        {
            mounts = (await store.GetAllAsync()).ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SMB 挂载配置读取失败，跳过启动重挂");
            coordinator.MarkStartupReady();
            return;
        }

        foreach (var mount in mounts.Where(m => m.Enabled))
        {
            // 白名单注册（无论当前是否挂载，命中后才会在状态页展示）
            SystemStatusProvider.ManagedMountPoints[mount.LocalPath.Trim().TrimEnd('/')] = 0;
            try
            {
                mountService.EnsureCredentialFile(mount);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SMB 凭据文件写入失败：{Name}", mount.Name);
            }
        }

        // 配置清单注册完成后即可启动健康监控；正在重试的路径通过 StartupRunning 防止并发恢复。
        coordinator.MarkStartupReady();

        foreach (var mount in mounts.Where(m => m.Enabled && m.AutoMount))
        {
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            if (mountService.GetStatus(mount) == SmbMountStatus.Mounted)
            {
                logger.LogInformation("SMB 已挂载，跳过重挂：{Name} → {LocalPath}", mount.Name, mount.LocalPath);
                continue;
            }

            var localPath = mount.LocalPath.Trim().TrimEnd('/');
            coordinator.BeginStartup(localPath);
            try
            {
                await AutoMountWithRetryAsync(mount, stoppingToken);
            }
            finally
            {
                coordinator.EndStartup(localPath);
            }
        }
    }

    private async Task AutoMountWithRetryAsync(SmbMount mount, CancellationToken stoppingToken)
    {
        var retryCount = 0;
        var pipeline = new ResiliencePipelineBuilder()
            .AddTimeout(TimeSpan.FromSeconds(MountAttemptTimeoutSeconds))
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = MaxRetries,
                ShouldHandle = new PredicateBuilder()
                    .Handle<SmbMountRetryException>()
                    .Handle<TimeoutRejectedException>(),
                DelayGenerator = args =>
                {
                    var attempt = args.AttemptNumber + 1;
                    var delay = attempt <= 5
                        ? TimeSpan.FromMinutes(attempt)
                        : TimeSpan.FromMinutes((attempt - 5) * 10);
                    return new ValueTask<TimeSpan?>(delay);
                },
                OnRetry = args =>
                {
                    retryCount = args.AttemptNumber + 1;
                    logger.LogWarning(args.Outcome.Exception,
                        "SMB 自动挂载失败，将在 {Delay} 后进行第 {Attempt}/{Max} 次重试：{Name} → {LocalPath}",
                        args.RetryDelay, retryCount, MaxRetries, mount.Name, mount.LocalPath);
                    return default;
                },
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 1.0,
                MinimumThroughput = MaxRetries + 1,
                SamplingDuration = TimeSpan.FromHours(1),
                BreakDuration = TimeSpan.FromHours(1),
                ShouldHandle = new PredicateBuilder().Handle<SmbMountRetryException>(),
            })
            .Build();

        try
        {
            await pipeline.ExecuteAsync(async cancellationToken =>
            {
                if (mountService.GetStatus(mount) == SmbMountStatus.Mounted)
                {
                    return;
                }

                await coordinator.RunWithMountLockAsync(mount.LocalPath, async () =>
                {
                    if (mountService.GetStatus(mount) == SmbMountStatus.Mounted)
                    {
                        return;
                    }

                    var (success, message) = await mountService.MountAsync(mount);
                    if (!success)
                    {
                        throw new SmbMountRetryException(message);
                    }
                }, cancellationToken);
            }, stoppingToken);

            logger.LogInformation("SMB 自动重挂成功：{Name} → {LocalPath}，重试次数：{Retries}",
                mount.Name, mount.LocalPath, retryCount);
        }
        catch (BrokenCircuitException ex)
        {
            logger.LogError(ex, "SMB 自动重挂连续失败，熔断并停止重试：{Name} → {LocalPath}", mount.Name, mount.LocalPath);
        }
        catch (SmbMountRetryException ex)
        {
            logger.LogError(ex, "SMB 自动重挂在 {Attempts} 次尝试后仍失败，停止重试：{Name} → {LocalPath}",
                MaxRetries + 1, mount.Name, mount.LocalPath);
        }
        catch (TimeoutRejectedException ex)
        {
            logger.LogError(ex, "SMB 自动重挂超时并停止：{Name} → {LocalPath}", mount.Name, mount.LocalPath);
        }
    }

    private sealed class SmbMountRetryException(string message) : Exception(message);
}
