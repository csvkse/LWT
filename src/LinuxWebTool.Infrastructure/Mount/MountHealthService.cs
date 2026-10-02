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

    /// <summary>文件系统容量探测（statfs）：目录可读但该请求卡死/失败，说明 SMB 会话已退化，需重挂恢复。</summary>
    Task<(bool Success, string? Error)> IsCapacityProbeOkAsync(SmbMount mount, CancellationToken cancellationToken = default);
}

/// <summary>
/// Legacy explicit checker retained for existing contract tests. Not registered by the application.
/// All application startup, periodic and manual operations use MountStateMachineService.
/// </summary>
public sealed class MountHealthService(
    SmbMountStore? store,
    ISmbMountOperations operations,
    IMountRuntimeProbe runtimeProbe,
    MountOperationCoordinator coordinator,
    IOperationLogger operationLogger,
    ILogger<MountHealthService> logger)
{
    private readonly ConcurrentDictionary<string, MountHealthSnapshot> _snapshots = new(StringComparer.Ordinal);

    public MountHealthSnapshot? GetSnapshot(string localPath) =>
        _snapshots.TryGetValue(MountOperationCoordinator.NormalizePath(localPath), out var snapshot) ? snapshot : null;

    public IReadOnlyList<MountHealthSnapshot> GetSnapshots() => [.. _snapshots.Values];

    public void SetSnapshot(MountHealthSnapshot snapshot) =>
        _snapshots[MountOperationCoordinator.NormalizePath(snapshot.LocalPath)] = snapshot;

    public void RemoveSnapshot(string localPath) =>
        _snapshots.TryRemove(MountOperationCoordinator.NormalizePath(localPath), out _);

    public async Task<MountHealthSnapshot> CheckMountAsync(SmbMount mount, CancellationToken cancellationToken = default)
    {
        if (store is not null) throw new InvalidOperationException("Use MountStateMachineService for managed mounts");
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
