using System.Diagnostics;
using LinuxWebTool.Contracts.Models;
using LinuxWebTool.Infrastructure.Mount;

namespace LinuxWebTool.Infrastructure.SystemInfo;

/// <summary>
/// 后台磁盘采集：读 mountinfo 只做字符串解析，容量逐挂载点调用有超时的 df。
/// 失效 SMB 只保留健康状态，不做容量探测；全局 df 永远不会被系统状态请求触发。
/// </summary>
public sealed class DiskStatusCacheService(
    DiskStatusCache cache,
    MountHealthService mountHealth,
    ILogger<DiskStatusCacheService> logger) : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly int ProbeTimeoutMs = 5_000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RefreshInterval);
        do
        {
            try
            {
                cache.SetSnapshot(await CollectAsync(stoppingToken));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "系统状态磁盘缓存刷新失败");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task<IReadOnlyList<DiskStatus>> CollectAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return CollectWindowsDisks();
        }

        if (!OperatingSystem.IsLinux())
        {
            return [];
        }

        var metadata = ReadMountInfo();
        var disks = new List<DiskStatus>();
        foreach (var mount in metadata)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isManaged = SystemStatusProvider.ManagedMountPoints.ContainsKey(
                MountOperationCoordinator.NormalizePath(mount.MountPoint));
            if (!mount.IsLocal && mount.MountPoint != "/" && !isManaged)
            {
                continue;
            }

            var health = mountHealth.GetSnapshot(mount.MountPoint);
            if (isManaged && health is { State: not MountHealthState.Healthy })
            {
                disks.Add(CreateUnavailableDisk(mount, health));
                continue;
            }

            var capacity = await ProbeCapacityAsync(mount, cancellationToken);
            disks.Add(capacity.Success
                ? capacity.Disk!
                : new DiskStatus
                {
                    Mount = mount.MountPoint,
                    FileSystem = mount.FileSystem,
                    Health = MountHealthState.Unknown.ToString(),
                    Error = capacity.Error ?? "磁盘容量探测失败",
                    LastCheckedAt = DateTime.Now,
                });
        }

        foreach (var health in mountHealth.GetSnapshots())
        {
            if (disks.Any(d => MountOperationCoordinator.NormalizePath(d.Mount) == health.LocalPath))
            {
                continue;
            }

            disks.Add(new DiskStatus
            {
                Mount = health.LocalPath,
                FileSystem = "cifs",
                Health = health.State.ToString(),
                Error = health.LastError,
                LastCheckedAt = health.LastCheckedAt,
            });
        }

        return disks;
    }

    private static List<DiskStatus> CollectWindowsDisks()
    {
        return DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
            .Select(d =>
            {
                var total = d.TotalSize;
                var free = d.AvailableFreeSpace;
                return new DiskStatus
                {
                    Mount = d.Name,
                    FileSystem = d.DriveFormat,
                    TotalBytes = total,
                    UsedBytes = total - free,
                    FreeBytes = free,
                    UsagePercent = total > 0 ? Math.Round(100.0 * (total - free) / total, 1) : 0,
                    Health = MountHealthState.Healthy.ToString(),
                    LastCheckedAt = DateTime.Now,
                };
            })
            .ToList();
    }

    private static IReadOnlyList<MountMetadata> ReadMountInfo()
    {
        var content = TryReadFile("/proc/1/mountinfo") ?? TryReadFile("/proc/self/mountinfo");
        return content is null ? [] : LinuxMountInfoParser.Parse(content);
    }

    private static string? TryReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private static DiskStatus CreateUnavailableDisk(MountMetadata mount, MountHealthSnapshot health) => new()
    {
        Mount = mount.MountPoint,
        FileSystem = mount.FileSystem,
        Health = health.State.ToString(),
        Error = health.LastError,
        LastCheckedAt = health.LastCheckedAt,
    };

    private static async Task<(bool Success, DiskStatus? Disk, string? Error)> ProbeCapacityAsync(
        MountMetadata mount,
        CancellationToken cancellationToken)
    {
        var output = await TryHostDfAsync(mount.MountPoint, cancellationToken)
            ?? await RunAsync("df", ["-kP", "--", mount.MountPoint], ProbeTimeoutMs, cancellationToken);
        if (string.IsNullOrWhiteSpace(output))
        {
            return (false, null, "df 无输出或执行超时");
        }

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 6)
            {
                continue;
            }

            var totalKb = ParseLong(parts[1]);
            var usedKb = ParseLong(parts[2]);
            var freeKb = ParseLong(parts[3]);
            if (totalKb <= 0)
            {
                continue;
            }

            return (true, new DiskStatus
            {
                Mount = mount.MountPoint,
                FileSystem = mount.FileSystem,
                TotalBytes = totalKb * 1024,
                UsedBytes = usedKb * 1024,
                FreeBytes = freeKb * 1024,
                UsagePercent = Math.Round(100.0 * usedKb / totalKb, 1),
                Health = MountHealthState.Healthy.ToString(),
                LastCheckedAt = DateTime.Now,
            }, null);
        }

        return (false, null, "df 输出无法解析");
    }

    private static async Task<string?> TryHostDfAsync(string mountPoint, CancellationToken cancellationToken)
    {
        if (!File.Exists("/proc/1/ns/mnt") || !File.Exists("/usr/bin/nsenter"))
        {
            return null;
        }

        return await RunAsync(
            "/usr/bin/nsenter",
            ["-t", "1", "-m", "--", "/usr/bin/df", "-kP", "--", mountPoint],
            ProbeTimeoutMs,
            cancellationToken);
    }

    private static async Task<string?> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), cancellationToken);
            }
            catch (TimeoutException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            var stdout = await stdoutTask;
            await stderrTask;
            return process.HasExited && process.ExitCode == 0 ? stdout : null;
        }
        catch
        {
            return null;
        }
    }

    private static long ParseLong(string? value) =>
        long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var result)
            ? result
            : 0;
}
