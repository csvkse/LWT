using System.Collections.Concurrent;
using LinuxWebTool.Contracts.Models;
using LinuxWebTool.Infrastructure.Persistence;
using LinuxWebTool.Infrastructure.Persistence.Entities;

namespace LinuxWebTool.Infrastructure.Mount;

public interface ISmbMountOperations
{
    SmbMountStatus GetStatus(SmbMount mount);

    Task<(bool Success, string Message)> MountAsync(SmbMount mount);

    Task<(bool Success, string Message)> UnmountAsync(SmbMount mount, bool lazy);
}

public interface IMountRuntimeProbe
{
    Task<bool> IsServerReachableAsync(SmbMount mount, CancellationToken cancellationToken = default);

    Task<(bool Success, string? Error)> IsFileSystemAccessibleAsync(SmbMount mount, CancellationToken cancellationToken = default);
}

/// <summary>
/// 运行期挂载健康监控。先探测、后恢复；服务器不可达绝不卸载，
/// 文件系统连续三次不可访问才懒卸载并重挂。所有操作与启动重挂共享挂载点锁。
/// </summary>
public sealed class MountHealthService(
    SmbMountStore? store,
    ISmbMountOperations operations,
    IMountRuntimeProbe runtimeProbe,
    MountOperationCoordinator coordinator,
    ILogger<MountHealthService> logger) : BackgroundService
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, MountHealthSnapshot> _snapshots = new(StringComparer.Ordinal);

    public MountHealthSnapshot? GetSnapshot(string localPath) =>
        _snapshots.TryGetValue(MountOperationCoordinator.NormalizePath(localPath), out var snapshot) ? snapshot : null;

    public IReadOnlyList<MountHealthSnapshot> GetSnapshots() => [.. _snapshots.Values];

    public void SetSnapshot(MountHealthSnapshot snapshot) =>
        _snapshots[MountOperationCoordinator.NormalizePath(snapshot.LocalPath)] = snapshot;

    public void RemoveSnapshot(string localPath) =>
        _snapshots.TryRemove(MountOperationCoordinator.NormalizePath(localPath), out _);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await coordinator.WaitStartupReadyAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(ProbeInterval);
        do
        {
            try
            {
                await RefreshAllAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "挂载健康巡检失败");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        if (store is null)
        {
            return;
        }

        var mounts = (await store.GetAllAsync()).Where(m => m.Enabled).ToArray();
        foreach (var mount in mounts)
        {
            await CheckMountAsync(mount, cancellationToken);
        }
    }

    public async Task<MountHealthSnapshot> CheckMountAsync(SmbMount mount, CancellationToken cancellationToken = default)
    {
        var localPath = MountOperationCoordinator.NormalizePath(mount.LocalPath);
        var snapshot = await coordinator.RunWithMountLockAsync(localPath, () => CheckLockedAsync(mount, cancellationToken), cancellationToken);
        _snapshots[localPath] = snapshot;
        return snapshot;
    }

    private async Task<MountHealthSnapshot> CheckLockedAsync(SmbMount mount, CancellationToken cancellationToken)
    {
        var previous = _snapshots.TryGetValue(MountOperationCoordinator.NormalizePath(mount.LocalPath), out var found)
            ? found
            : new MountHealthSnapshot { LocalPath = MountOperationCoordinator.NormalizePath(mount.LocalPath) };
        if (coordinator.IsStartupRunning(mount.LocalPath))
        {
            return previous ?? Create(mount, MountHealthState.Recovering, error: null);
        }

        if (previous.State == MountHealthState.RecoveryFailed
            && previous.NextAttemptAt.HasValue
            && previous.NextAttemptAt.Value > DateTime.Now)
        {
            return previous;
        }

        var status = operations.GetStatus(mount);
        if (status == SmbMountStatus.Unsupported)
        {
            return Update(previous, mount, MountHealthState.Unsupported, "当前系统不支持挂载管理");
        }

        if (status == SmbMountStatus.NotMounted)
        {
            if (!mount.AutoMount || !mount.Enabled)
            {
                return Update(previous, mount, MountHealthState.NotMounted, null);
            }

            var mounted = await operations.MountAsync(mount);
            return mounted.Success
                ? Update(previous, mount, MountHealthState.Healthy, null)
                : Fail(previous, mount, $"自动挂载失败：{mounted.Message}", previous.RecoveryAttemptCount + 1);
        }

        var serverReachable = await runtimeProbe.IsServerReachableAsync(mount, cancellationToken);
        if (!serverReachable)
        {
            var unreachable = Update(
                previous,
                mount,
                MountHealthState.ServerUnreachable,
                "SMB 服务器 445 端口不可达",
                failureCount: 0);
            logger.LogWarning("SMB 服务器不可达，保留挂载表并等待恢复：{Name} → {LocalPath}", mount.Name, mount.LocalPath);
            return unreachable;
        }

        var accessible = await runtimeProbe.IsFileSystemAccessibleAsync(mount, cancellationToken);
        if (accessible.Success)
        {
            return Update(previous, mount, MountHealthState.Healthy, null);
        }

        var failureCount = previous.FailureCount + 1;
        if (failureCount < 3 || !mount.AutoMount || !mount.Enabled)
        {
            return Update(
                previous,
                mount,
                MountHealthState.Stale,
                accessible.Error ?? "文件系统访问超时",
                failureCount: failureCount);
        }

        var recovered = await RecoverAsync(mount, cancellationToken);
        if (recovered.Success)
        {
            logger.LogInformation("SMB 挂载自动恢复成功：{Name} → {LocalPath}", mount.Name, mount.LocalPath);
            return Update(previous, mount, MountHealthState.Healthy, null, failureCount: 0);
        }

        logger.LogWarning("SMB 挂载自动恢复失败：{Name} → {LocalPath}，{Error}", mount.Name, mount.LocalPath, recovered.Message);
        return Fail(previous, mount, recovered.Message, previous.RecoveryAttemptCount + 1);
    }

    private async Task<(bool Success, string Message)> RecoverAsync(SmbMount mount, CancellationToken cancellationToken)
    {
        var unmount = await operations.UnmountAsync(mount, lazy: true);
        if (!unmount.Success)
        {
            return (false, $"懒卸载失败：{unmount.Message}");
        }

        var mountResult = await operations.MountAsync(mount);
        return mountResult.Success
            ? (true, mountResult.Message)
            : (false, $"重新挂载失败：{mountResult.Message}");
    }

    private static MountHealthSnapshot Create(
        SmbMount mount,
        MountHealthState state,
        string? error,
        int failureCount = 0,
        int recoveryAttemptCount = 0,
        DateTime? nextAttemptAt = null) => new()
    {
        LocalPath = MountOperationCoordinator.NormalizePath(mount.LocalPath),
        State = state,
        LastCheckedAt = DateTime.Now,
        LastError = error,
        FailureCount = failureCount,
        RecoveryAttemptCount = recoveryAttemptCount,
        NextAttemptAt = nextAttemptAt,
    };

    private static MountHealthSnapshot Update(
        MountHealthSnapshot previous,
        SmbMount mount,
        MountHealthState state,
        string? error,
        int? failureCount = null) => new()
    {
        LocalPath = MountOperationCoordinator.NormalizePath(mount.LocalPath),
        State = state,
        LastCheckedAt = DateTime.Now,
        LastError = error,
        FailureCount = failureCount ?? previous.FailureCount,
        RecoveryAttemptCount = state == MountHealthState.Healthy ? 0 : previous.RecoveryAttemptCount,
        NextAttemptAt = null,
    };

    private static MountHealthSnapshot Fail(
        MountHealthSnapshot previous,
        SmbMount mount,
        string error,
        int recoveryAttemptCount)
    {
        var attempt = Math.Max(1, recoveryAttemptCount);
        var delay = TimeSpan.FromSeconds(Math.Min(600, 15 * Math.Pow(2, attempt - 1)));
        return new MountHealthSnapshot
        {
            LocalPath = MountOperationCoordinator.NormalizePath(mount.LocalPath),
            State = MountHealthState.RecoveryFailed,
            LastCheckedAt = DateTime.Now,
            LastError = error,
            FailureCount = previous.FailureCount,
            RecoveryAttemptCount = recoveryAttemptCount,
            NextAttemptAt = DateTime.Now.Add(delay),
        };
    }
}
