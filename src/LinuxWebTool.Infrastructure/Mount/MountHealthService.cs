using System.Collections.Concurrent;
using System.Threading.Channels;
using LinuxWebTool.Contracts.Models;
using LinuxWebTool.Infrastructure.Persistence;
using LinuxWebTool.Infrastructure.Persistence.Entities;
using LinuxWebTool.Infrastructure.SystemInfo;

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

    /// <summary>文件系统容量探测（statfs）：目录可读但该请求卡死/失败，说明 SMB 会话已退化，需重挂恢复。</summary>
    Task<(bool Success, string? Error)> IsCapacityProbeOkAsync(SmbMount mount, CancellationToken cancellationToken = default);
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
    IOperationLogger operationLogger,
    ILogger<MountHealthService> logger) : BackgroundService
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, MountHealthSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _pendingChecks = new(StringComparer.Ordinal);
    private readonly Channel<string> _checkRequests = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    public void RequestImmediateCheck(string localPath)
    {
        var path = MountOperationCoordinator.NormalizePath(localPath);
        if (!SystemStatusProvider.ManagedMountPoints.ContainsKey(path)) return;
        if (_pendingChecks.TryAdd(path, 0) && !_checkRequests.Writer.TryWrite(path))
        {
            _pendingChecks.TryRemove(path, out _);
        }
    }

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

        _ = ProcessRequestsAsync(stoppingToken);
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

    private async Task ProcessRequestsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var path in _checkRequests.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    if (store is null) continue;
                    var mount = (await store.GetAllAsync()).FirstOrDefault(m =>
                        m.Enabled && MountOperationCoordinator.NormalizePath(m.LocalPath) == path);
                    if (mount is not null) await CheckMountAsync(mount, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                catch (Exception ex) { logger.LogWarning(ex, "SMB 挂载即时复查失败：{LocalPath}", path); }
                finally { _pendingChecks.TryRemove(path, out _); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
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

        if (status == SmbMountStatus.Abnormal)
        {
            return Update(previous, mount, MountHealthState.Stale, "挂载点被非 CIFS 文件系统占用", failureCount: 0);
        }

        if (status == SmbMountStatus.NotMounted)
        {
            if (!mount.AutoMount || !mount.Enabled)
            {
                return Update(previous, mount, MountHealthState.NotMounted, null);
            }

            var mounted = await operations.MountAsync(mount);
            if (!mounted.Success)
            {
                await LogHealthActionAsync(mount, "健康检测-自动挂载", $"自动挂载失败：{mounted.Message}", success: false);
                return Fail(previous, mount, $"自动挂载失败：{mounted.Message}", previous.RecoveryAttemptCount + 1);
            }

            var mountedProbe = await runtimeProbe.IsFileSystemAccessibleAsync(mount, cancellationToken);
            if (!mountedProbe.Success)
            {
                await LogHealthActionAsync(mount, "健康检测-自动挂载", $"自动挂载后目录不可访问：{mountedProbe.Error}", success: false);
                return Fail(previous, mount, $"自动挂载后目录不可访问：{mountedProbe.Error}", previous.RecoveryAttemptCount + 1);
            }

            await LogHealthActionAsync(mount, "健康检测-自动挂载", "自动挂载完成", success: true);
            return Update(previous, mount, MountHealthState.Healthy, null, failureCount: 0);
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
        if (!accessible.Success)
        {
            return await HandleProbeFailureAsync(previous, mount, accessible.Error ?? "文件系统访问超时", cancellationToken);
        }

        // 目录可读不一定代表会话健康：statfs 每次必须上线路由，能暴露目录缓存掩盖的退化连接。
        var capacityOk = await runtimeProbe.IsCapacityProbeOkAsync(mount, cancellationToken);
        if (capacityOk.Success)
        {
            return Update(previous, mount, MountHealthState.Healthy, null, failureCount: 0);
        }

        return await HandleProbeFailureAsync(previous, mount, capacityOk.Error ?? "文件系统容量探测超时", cancellationToken);
    }

    /// <summary>探测失败累计到三次且允许自动挂载时，懒卸载并重挂恢复；否则保持 Stale 等待。</summary>
    private async Task<MountHealthSnapshot> HandleProbeFailureAsync(
        MountHealthSnapshot previous,
        SmbMount mount,
        string error,
        CancellationToken cancellationToken)
    {
        var failureCount = previous.FailureCount + 1;
        if (failureCount < 3 || !mount.AutoMount || !mount.Enabled)
        {
            return Update(previous, mount, MountHealthState.Stale, error, failureCount: failureCount);
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
            await LogHealthActionAsync(mount, "健康检测-自动重挂", $"懒卸载失败：{unmount.Message}", success: false);
            return (false, $"懒卸载失败：{unmount.Message}");
        }

        var mountResult = await operations.MountAsync(mount);
        if (!mountResult.Success)
        {
            await LogHealthActionAsync(mount, "健康检测-自动重挂", $"重新挂载失败：{mountResult.Message}", success: false);
            return (false, $"重新挂载失败：{mountResult.Message}");
        }

        var probe = await runtimeProbe.IsFileSystemAccessibleAsync(mount, cancellationToken);
        if (!probe.Success)
        {
            await LogHealthActionAsync(mount, "健康检测-自动重挂", $"重挂后目录不可访问：{probe.Error}", success: false);
            return (false, $"重新挂载后目录不可访问：{probe.Error}");
        }

        // 只验目录可读可能掩盖未恢复的退化会话，重挂后 statfs 仍卡死视为恢复失败，进入退避等待。
        var capacityOk = await runtimeProbe.IsCapacityProbeOkAsync(mount, cancellationToken);
        if (!capacityOk.Success)
        {
            await LogHealthActionAsync(mount, "健康检测-自动重挂", $"重挂后容量探测仍失败：{capacityOk.Error}", success: false);
            return (false, $"重新挂载后容量探测仍失败：{capacityOk.Error}");
        }

        await LogHealthActionAsync(mount, "健康检测-自动重挂", "懒卸载 → 重挂 → 目录与容量验证通过", success: true);
        return (true, mountResult.Message);
    }

    /// <summary>健康检测触发的自动动作写入操作日志，与手动挂载/卸载入口保持同样的审计口径。</summary>
    private async Task LogHealthActionAsync(SmbMount mount, string action, string detail, bool success)
    {
        await operationLogger.LogAsync(action, "SMB挂载", mount.Name,
            $"{mount.Server} → {mount.LocalPath}；{detail}", success);
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
