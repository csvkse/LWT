using System.Collections.Concurrent;
using System.Threading.Channels;
using LinuxWebTool.Contracts.Models;
using LinuxWebTool.Infrastructure.Persistence;
using LinuxWebTool.Infrastructure.Persistence.Entities;
using LinuxWebTool.Infrastructure.SystemInfo;

namespace LinuxWebTool.Infrastructure.Mount;

public sealed class WebDavMountHealthService(
    WebDavMountStore store,
    WebDavMountService operations,
    WebDavMountProbe probe,
    WebDavMountStartupService startup,
    MountOperationCoordinator coordinator,
    IOperationLogger operationLogger,
    ILogger<WebDavMountHealthService> logger) : BackgroundService
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
        await startup.WaitReadyAsync(stoppingToken);
        _ = ProcessRequestsAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                foreach (var mount in (await store.GetAllAsync()).Where(m => m.Enabled))
                    await CheckMountAsync(mount, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "WebDAV 健康巡检失败"); }
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
                catch (Exception ex) { logger.LogWarning(ex, "WebDAV 即时复查失败：{Path}", path); }
                finally { _pending.TryRemove(path, out _); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public async Task<MountHealthSnapshot> CheckMountAsync(WebDavMount mount, CancellationToken cancellationToken = default)
    {
        var path = MountOperationCoordinator.NormalizePath(mount.LocalPath);
        var snapshot = await coordinator.RunWithMountLockAsync(path,
            () => CheckLockedAsync(mount, cancellationToken), cancellationToken);
        _snapshots[path] = snapshot;
        return snapshot;
    }

    private async Task<MountHealthSnapshot> CheckLockedAsync(WebDavMount mount, CancellationToken cancellationToken)
    {
        var path = MountOperationCoordinator.NormalizePath(mount.LocalPath);
        var previous = GetSnapshot(path) ?? new MountHealthSnapshot { LocalPath = path };
        if (coordinator.IsStartupRunning(path)) return previous;
        if (!WebDavMountService.IsSupported) return Make(mount, MountHealthState.Unsupported, "Linux FUSE 挂载能力不可用");
        if (previous.State == MountHealthState.RecoveryFailed && previous.NextAttemptAt > DateTime.Now) return previous;
        var status = operations.GetStatus(mount);
        if (status == SmbMountStatus.Unsupported) return Make(mount, MountHealthState.Unsupported, "仅支持 Linux FUSE 挂载");
        if (status == SmbMountStatus.Abnormal) return Make(mount, MountHealthState.Stale, "挂载点被其他文件系统占用");
        if (status == SmbMountStatus.NotMounted)
        {
            if (!mount.Enabled || !mount.AutoMount || _manualUnmounts.ContainsKey(path))
                return Make(mount, MountHealthState.NotMounted, null);
            var remote = await probe.ProbeRemoteAsync(mount, cancellationToken);
            if (!remote.Success)
                return Make(mount, remote.CredentialsRejected ? MountHealthState.Stale : MountHealthState.ServerUnreachable, remote.Error);
            var mounted = await operations.MountAsync(mount, cancellationToken);
            if (!mounted.Success)
            {
                await LogActionAsync(mount, "健康检测-自动挂载", mounted.Message, false);
                return Fail(mount, previous, mounted.Message);
            }
            var mountedHealth = await VerifyAfterMountAsync(mount, previous, cancellationToken);
            await LogActionAsync(mount, "健康检测-自动挂载", mountedHealth.LastError ?? "挂载后验证通过",
                mountedHealth.State == MountHealthState.Healthy);
            return mountedHealth;
        }

        var remoteProbe = await probe.ProbeRemoteAsync(mount, cancellationToken);
        if (!remoteProbe.Success)
            return Make(mount, remoteProbe.CredentialsRejected ? MountHealthState.Stale : MountHealthState.ServerUnreachable, remoteProbe.Error);
        var localProbe = await probe.ProbeLocalAsync(mount, cancellationToken);
        if (localProbe.Success) return Make(mount, MountHealthState.Healthy, null);

        var failures = previous.FailureCount + 1;
        if (failures < 3 || !mount.AutoMount || !mount.Enabled || _manualUnmounts.ContainsKey(path))
            return Make(mount, MountHealthState.Stale, localProbe.Error, failures);
        // 写缓存可能尚未回传；busy 时保留旧进程，避免新旧挂载共用缓存。
        var unmounted = await operations.UnmountAsync(mount, lazy: false, cancellationToken: cancellationToken);
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
        if (recoveryHealth.State == MountHealthState.Healthy)
            logger.LogInformation("WebDAV 自动恢复：{Name} → {Path}", mount.Name, path);
        await LogActionAsync(mount, "健康检测-自动重挂", recoveryHealth.LastError ?? "重挂后验证通过",
            recoveryHealth.State == MountHealthState.Healthy);
        return recoveryHealth;
    }

    private async Task<MountHealthSnapshot> VerifyAfterMountAsync(WebDavMount mount, MountHealthSnapshot previous, CancellationToken cancellationToken)
    {
        var remote = await probe.ProbeRemoteAsync(mount, cancellationToken);
        var local = await probe.ProbeLocalAsync(mount, cancellationToken);
        return remote.Success && local.Success
            ? Make(mount, MountHealthState.Healthy, null)
            : Fail(mount, previous, remote.Error ?? local.Error ?? "挂载后验证失败");
    }

    private static MountHealthSnapshot Make(WebDavMount mount, MountHealthState state, string? error, int failures = 0) => new()
    {
        LocalPath = MountOperationCoordinator.NormalizePath(mount.LocalPath),
        State = state,
        LastCheckedAt = DateTime.Now,
        LastError = error,
        FailureCount = failures,
    };

    private Task LogActionAsync(WebDavMount mount, string action, string detail, bool success) =>
        operationLogger.LogAsync(action, "WebDAV挂载", mount.Name,
            $"{mount.Url} → {mount.LocalPath}；{detail}", success);

    private static MountHealthSnapshot Fail(WebDavMount mount, MountHealthSnapshot previous, string error)
    {
        var attempts = previous.RecoveryAttemptCount + 1;
        return new MountHealthSnapshot
        {
            LocalPath = MountOperationCoordinator.NormalizePath(mount.LocalPath),
            State = MountHealthState.RecoveryFailed,
            LastCheckedAt = DateTime.Now,
            LastError = error,
            FailureCount = previous.FailureCount,
            RecoveryAttemptCount = attempts,
            NextAttemptAt = DateTime.Now.AddSeconds(Math.Min(600, 15 * Math.Pow(2, attempts - 1))),
        };
    }
}
