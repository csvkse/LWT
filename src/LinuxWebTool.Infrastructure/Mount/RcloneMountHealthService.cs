using System.Collections.Concurrent;
using System.Threading.Channels;
using LinuxWebTool.Contracts.Models;
using LinuxWebTool.Infrastructure.Persistence;
using LinuxWebTool.Infrastructure.Persistence.Entities;
using LinuxWebTool.Infrastructure.SystemInfo;

namespace LinuxWebTool.Infrastructure.Mount;

/// <summary>SFTP/S3 挂载健康巡检：远端直连列目录 + 本地 FUSE 列目录。</summary>
public sealed class RcloneMountHealthService(
    RcloneMountStore store,
    RcloneMountService operations,
    MountOperationCoordinator coordinator,
    IOperationLogger operationLogger,
    ILogger<RcloneMountHealthService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, MountHealthSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _manualUnmounts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.Ordinal);
    private readonly Channel<string> _requests = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    public MountHealthSnapshot? GetSnapshot(string path) =>
        _snapshots.TryGetValue(MountOperationCoordinator.NormalizePath(path), out var value) ? value : null;
    public IReadOnlyList<MountHealthSnapshot> GetSnapshots() => [.. _snapshots.Values];
    public void RemoveSnapshot(string path) => _snapshots.TryRemove(MountOperationCoordinator.NormalizePath(path), out _);
    public void SuppressAutoMount(string path) => _manualUnmounts[MountOperationCoordinator.NormalizePath(path)] = 0;
    public void ResumeAutoMount(string path) => _manualUnmounts.TryRemove(MountOperationCoordinator.NormalizePath(path), out _);

    public void RequestImmediateCheck(string path)
    {
        path = MountOperationCoordinator.NormalizePath(path);
        if (!SystemStatusProvider.ManagedMountPoints.ContainsKey(path)) return;
        if (_pending.TryAdd(path, 0) && !_requests.Writer.TryWrite(path)) _pending.TryRemove(path, out _);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(2000, stoppingToken);
        _ = ProcessRequestsAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                foreach (var mount in (await store.GetAllAsync()).Where(m => m.Enabled))
                {
                    SystemStatusProvider.ManagedMountPoints[MountOperationCoordinator.NormalizePath(mount.LocalPath)] = 0;
                    await CheckMountAsync(mount, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "SFTP/S3 健康巡检失败"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ProcessRequestsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var path in _requests.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    var mount = (await store.GetAllAsync()).FirstOrDefault(m =>
                        m.Enabled && MountOperationCoordinator.NormalizePath(m.LocalPath) == path);
                    if (mount is not null) await CheckMountAsync(mount, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                catch (Exception ex) { logger.LogWarning(ex, "SFTP/S3 即时复查失败：{Path}", path); }
                finally { _pending.TryRemove(path, out _); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public async Task<MountHealthSnapshot> CheckMountAsync(RcloneMount mount, CancellationToken cancellationToken = default)
    {
        var path = MountOperationCoordinator.NormalizePath(mount.LocalPath);
        var snapshot = await coordinator.RunWithMountLockAsync(path,
            () => CheckLockedAsync(mount, cancellationToken), cancellationToken);
        _snapshots[path] = snapshot;
        return snapshot;
    }

    private async Task<MountHealthSnapshot> CheckLockedAsync(RcloneMount mount, CancellationToken cancellationToken)
    {
        var path = MountOperationCoordinator.NormalizePath(mount.LocalPath);
        var previous = GetSnapshot(path) ?? new MountHealthSnapshot { LocalPath = path };
        if (!RcloneMountService.IsSupported) return Make(mount, MountHealthState.Unsupported, "Linux FUSE 挂载能力不可用");
        if (previous.State == MountHealthState.RecoveryFailed && previous.NextAttemptAt > DateTime.Now) return previous;
        var status = operations.GetStatus(mount);
        if (status == SmbMountStatus.Abnormal) return Make(mount, MountHealthState.Stale, "挂载点被其他文件系统占用");
        if (status == SmbMountStatus.NotMounted)
        {
            if (!mount.Enabled || !mount.AutoMount || _manualUnmounts.ContainsKey(path))
                return Make(mount, MountHealthState.NotMounted, null);
            var remote = await operations.ProbeRemoteAsync(mount, cancellationToken);
            if (!remote.Success)
                return Make(mount, remote.CredentialsRejected ? MountHealthState.Stale : MountHealthState.ServerUnreachable, remote.Error);
            var mounted = await operations.MountAsync(mount, cancellationToken);
            if (!mounted.Success)
            {
                await LogActionAsync(mount, "健康检测-自动挂载", mounted.Message, false);
                return Fail(mount, previous, mounted.Message);
            }
            var verified = await VerifyAfterMountAsync(mount, previous, cancellationToken);
            await LogActionAsync(mount, "健康检测-自动挂载", verified.LastError ?? "挂载后验证通过",
                verified.State == MountHealthState.Healthy);
            return verified;
        }

        var remoteProbe = await operations.ProbeRemoteAsync(mount, cancellationToken);
        if (!remoteProbe.Success)
            return Make(mount, remoteProbe.CredentialsRejected ? MountHealthState.Stale : MountHealthState.ServerUnreachable, remoteProbe.Error);
        var localProbe = await operations.ProbeLocalAsync(mount, cancellationToken);
        if (localProbe.Success) return Make(mount, MountHealthState.Healthy, null);
        var failures = previous.FailureCount + 1;
        if (failures < 3 || !mount.AutoMount || !mount.Enabled || _manualUnmounts.ContainsKey(path))
            return Make(mount, MountHealthState.Stale, localProbe.Error, failures);
        // 正常卸载；若写缓存仍被占用则不强行重挂。
        var unmounted = await operations.UnmountAsync(mount, cancellationToken);
        if (!unmounted.Success)
        {
            await LogActionAsync(mount, "健康检测-自动重挂", unmounted.Message, false);
            return Fail(mount, previous, unmounted.Message);
        }
        var recovered = await operations.MountAsync(mount, cancellationToken);
        if (!recovered.Success)
        {
            await LogActionAsync(mount, "健康检测-自动重挂", recovered.Message, false);
            return Fail(mount, previous, recovered.Message);
        }
        var recoveryHealth = await VerifyAfterMountAsync(mount, previous, cancellationToken);
        await LogActionAsync(mount, "健康检测-自动重挂", recoveryHealth.LastError ?? "重挂后验证通过",
            recoveryHealth.State == MountHealthState.Healthy);
        return recoveryHealth;
    }

    private async Task<MountHealthSnapshot> VerifyAfterMountAsync(RcloneMount mount, MountHealthSnapshot previous,
        CancellationToken cancellationToken)
    {
        var remote = await operations.ProbeRemoteAsync(mount, cancellationToken);
        var local = await operations.ProbeLocalAsync(mount, cancellationToken);
        return remote.Success && local.Success
            ? Make(mount, MountHealthState.Healthy, null)
            : Fail(mount, previous, remote.Error ?? local.Error ?? "挂载后验证失败");
    }

    private static MountHealthSnapshot Make(RcloneMount mount, MountHealthState state, string? error, int failures = 0) => new()
    {
        LocalPath = MountOperationCoordinator.NormalizePath(mount.LocalPath),
        State = state, LastCheckedAt = DateTime.Now, LastError = error, FailureCount = failures,
    };

    private static MountHealthSnapshot Fail(RcloneMount mount, MountHealthSnapshot previous, string error)
    {
        var attempts = previous.RecoveryAttemptCount + 1;
        return new MountHealthSnapshot
        {
            LocalPath = MountOperationCoordinator.NormalizePath(mount.LocalPath),
            State = MountHealthState.RecoveryFailed, LastCheckedAt = DateTime.Now, LastError = error,
            FailureCount = previous.FailureCount, RecoveryAttemptCount = attempts,
            NextAttemptAt = DateTime.Now.AddSeconds(Math.Min(600, 15 * Math.Pow(2, attempts - 1))),
        };
    }

    private Task LogActionAsync(RcloneMount mount, string action, string detail, bool success) =>
        operationLogger.LogAsync(action, mount.Kind.ToUpperInvariant() + "挂载", mount.Name,
            $"{mount.LocalPath}；{detail}", success);
}
