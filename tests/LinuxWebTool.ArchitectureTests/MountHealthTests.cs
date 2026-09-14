using LinuxWebTool.Contracts.Models;
using LinuxWebTool.Infrastructure.Mount;
using LinuxWebTool.Infrastructure.Persistence.Entities;
using LinuxWebTool.Infrastructure.SystemInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinuxWebTool.ArchitectureTests;

public sealed class MountHealthTests
{
    [Fact]
    public void Startup_retry_uses_quick_bounded_delays_then_hands_off_to_health_service()
    {
        Assert.Equal(3, SmbMountStartupRetryPlan.AttemptCount);
        Assert.Null(SmbMountStartupRetryPlan.GetDelayBeforeAttempt(1));
        Assert.Equal(TimeSpan.FromSeconds(5), SmbMountStartupRetryPlan.GetDelayBeforeAttempt(2));
        Assert.Equal(TimeSpan.FromSeconds(15), SmbMountStartupRetryPlan.GetDelayBeforeAttempt(3));
        Assert.Null(SmbMountStartupRetryPlan.GetDelayBeforeAttempt(4));
    }

    [Fact]
    public void Linux_mountinfo_parser_decodes_escapes_and_keeps_real_mounts()
    {
        const string content = """
            36 35 8:1 / / rw,relatime - ext4 /dev/sda1 rw
            80 36 0:67 / /mnt/escaped\040share\011dir rw - cifs //nas/share rw
            81 36 0:68 / /proc rw,relatime - proc proc rw
            """;

        var mounts = LinuxMountInfoParser.Parse(content).ToArray();

        Assert.Equal(2, mounts.Length);
        Assert.Equal("/", mounts[0].MountPoint);
        Assert.Equal("ext4", mounts[0].FileSystem);
        Assert.Equal("/dev/sda1", mounts[0].Source);
        Assert.True(mounts[0].IsLocal);
        Assert.Equal("/mnt/escaped share\tdir", mounts[1].MountPoint);
        Assert.Equal("cifs", mounts[1].FileSystem);
        Assert.False(mounts[1].IsLocal);
    }

    [Fact]
    public async Task System_status_provider_reads_disk_snapshot_from_cache()
    {
        var disks = new[]
        {
            new DiskStatus
            {
                Mount = "/",
                FileSystem = "ext4",
                TotalBytes = 100,
                UsedBytes = 25,
                FreeBytes = 75,
                UsagePercent = 25,
                Health = MountHealthState.Healthy.ToString(),
            },
        };
        var provider = new SystemStatusProvider(new SystemStatusOptions(), new TestDiskCache(disks));

        var status = await provider.GetStatusAsync();

        Assert.Single(status.Disks);
        Assert.Equal(disks[0].Mount, status.Disks[0].Mount);
        Assert.Equal(nameof(MountHealthState.Healthy), status.Disks[0].Health);
    }

    [Fact]
    public async Task Mount_health_service_waits_for_startup_service_readiness()
    {
        var coordinator = new MountOperationCoordinator();
        var waiting = coordinator.WaitStartupReadyAsync(CancellationToken.None);

        Assert.False(waiting.IsCompleted);
        coordinator.MarkStartupReady();
        await waiting;
    }

    [Fact]
    public async Task Server_unreachable_is_reported_without_unmounting()
    {
        var mount = CreateMount(autoMount: true);
        var operations = new TestMountOperations(SmbMountStatus.Mounted);
        var probes = new TestMountProbe { ServerReachable = false };
        var service = CreateHealthService(operations, probes);

        var health = await service.CheckMountAsync(mount, CancellationToken.None);

        Assert.Equal(MountHealthState.ServerUnreachable, health.State);
        Assert.Equal(0, operations.UnmountCalls);
        Assert.Equal(0, operations.MountCalls);
    }

    [Fact]
    public async Task Stale_mount_requires_three_confirmations_then_lazy_remounts()
    {
        var mount = CreateMount(autoMount: true);
        var operations = new TestMountOperations(SmbMountStatus.Mounted);
        var probes = new TestMountProbe { ServerReachable = true, FileSystemAccessible = false };
        var service = CreateHealthService(operations, probes);

        var first = await service.CheckMountAsync(mount, CancellationToken.None);
        var second = await service.CheckMountAsync(mount, CancellationToken.None);
        Assert.Equal(MountHealthState.Stale, first.State);
        Assert.Equal(MountHealthState.Stale, second.State);
        Assert.Equal(0, operations.UnmountCalls);

        var third = await service.CheckMountAsync(mount, CancellationToken.None);

        Assert.Equal(MountHealthState.Healthy, third.State);
        Assert.Equal(1, operations.UnmountCalls);
        Assert.True(operations.LastUnmountWasLazy);
        Assert.Equal(1, operations.MountCalls);
        Assert.Equal(0, third.FailureCount);
    }

    [Fact]
    public async Task Not_mounted_automount_retries_but_manual_mount_does_not()
    {
        var automatic = CreateMount(autoMount: true);
        var manual = CreateMount(autoMount: false);
        var operations = new TestMountOperations(SmbMountStatus.NotMounted);
        var probes = new TestMountProbe();
        var service = CreateHealthService(operations, probes);

        var automaticHealth = await service.CheckMountAsync(automatic, CancellationToken.None);
        var manualHealth = await service.CheckMountAsync(manual, CancellationToken.None);

        Assert.Equal(MountHealthState.Healthy, automaticHealth.State);
        Assert.Equal(MountHealthState.NotMounted, manualHealth.State);
        Assert.Equal(1, operations.MountCalls);
    }

    private static MountHealthService CreateHealthService(
        TestMountOperations operations,
        TestMountProbe probes)
    {
        return new MountHealthService(
            store: null!,
            operations,
            probes,
            new MountOperationCoordinator(),
            NullLogger<MountHealthService>.Instance);
    }

    private static SmbMount CreateMount(bool autoMount) => new()
    {
        Id = Guid.NewGuid(),
        Name = "test",
        Server = "//nas/share",
        LocalPath = "/mnt/test",
        AutoMount = autoMount,
        Enabled = true,
    };

    private sealed class TestDiskCache(IReadOnlyList<DiskStatus> disks) : IDiskStatusCache
    {
        public IReadOnlyList<DiskStatus> GetSnapshot() => disks;
    }

    private sealed class TestMountOperations(SmbMountStatus status) : ISmbMountOperations
    {
        public int MountCalls { get; private set; }

        public int UnmountCalls { get; private set; }

        public bool LastUnmountWasLazy { get; private set; }

        public SmbMountStatus GetStatus(SmbMount mount) => status;

        public Task<(bool Success, string Message)> MountAsync(SmbMount mount)
        {
            MountCalls++;
            return Task.FromResult((true, "mounted"));
        }

        public Task<(bool Success, string Message)> UnmountAsync(SmbMount mount, bool lazy)
        {
            UnmountCalls++;
            LastUnmountWasLazy = lazy;
            return Task.FromResult((true, "unmounted"));
        }
    }

    private sealed class TestMountProbe : IMountRuntimeProbe
    {
        public bool ServerReachable { get; init; } = true;

        public bool FileSystemAccessible { get; init; } = true;

        public Task<bool> IsServerReachableAsync(SmbMount mount, CancellationToken cancellationToken = default) =>
            Task.FromResult(ServerReachable);

        public Task<(bool Success, string? Error)> IsFileSystemAccessibleAsync(SmbMount mount, CancellationToken cancellationToken = default) =>
            Task.FromResult((FileSystemAccessible, FileSystemAccessible ? null : "probe timeout"));
    }
}
