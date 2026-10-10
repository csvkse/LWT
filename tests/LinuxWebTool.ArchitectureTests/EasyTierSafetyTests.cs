using System.Reflection;
using LinuxWebTool.Infrastructure.Features.EasyTier.Adapters;
using Xunit;

namespace LinuxWebTool.ArchitectureTests;

public sealed class EasyTierSafetyTests
{
    // Resolve the guard at runtime so absence is an assertion failure, not a compiler failure.
    private static void Validate(string toml, string[] active, string[] local)
    {
        var type = typeof(EasyTierNodeManager).Assembly.GetType("LinuxWebTool.Infrastructure.Features.EasyTier.Adapters.EasyTierNetworkGuard");
        Assert.NotNull(type);
        var method = type.GetMethod("Validate", BindingFlags.Static | BindingFlags.Public);
        Assert.NotNull(method);
        try { method.Invoke(null, [toml, active, local]); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); }
    }

    [Theory]
    [InlineData("10.126.126.2/24", "10.126.126.1/24")]
    [InlineData("10.126.126.2/24", "10.126.1.1/16")]
    [InlineData("10.126.126.2", "10.126.126.1/24")]
    public void LinuxArch020_Overlapping_virtual_networks_are_rejected(string candidate, string running)
        => Assert.Throws<InvalidOperationException>(() => Validate($"ipv4 = \"{candidate}\"\ndhcp = false", [running], []));

    [Fact]
    public void LinuxArch020_Local_network_overlap_is_rejected()
        => Assert.Throws<InvalidOperationException>(() => Validate("ipv4 = '10.126.126.2/24'", [], ["10.126.1.55/16"]));

    [Fact]
    public void LinuxArch020_Different_networks_are_allowed()
        => Validate("ipv4 = \"10.126.127.2/24\"", ["10.126.126.1/24"], ["192.168.1.1/24"]);

    [Fact]
    public void LinuxArch020_Quoted_keys_are_checked_and_escaped_keys_fail_closed()
    {
        Assert.Throws<InvalidOperationException>(() => Validate("\"ipv4\" = \"10.126.126.2/24\"", ["10.126.126.1/24"], []));
        Assert.Throws<InvalidOperationException>(() => Validate("\"\\u0069pv4\" = \"10.126.126.2/24\"", ["10.126.126.1/24"], []));
        Validate("dhcp = true", [], []); // Preserve single-node DHCP.
        Validate("ipv4 = \"10.126.126.2/32\"", ["10.126.126.1/32"], []);
        Assert.Throws<InvalidOperationException>(() => Validate("ipv4 = \"10.126.126.2/33\"", [], []));
    }

    [Fact]
    public void LinuxArch020_Raw_config_and_unknown_dhcp_cannot_bypass_guard()
    {
        Assert.Throws<InvalidOperationException>(() => Validate("ipv4 = \"10.126.126.9/24\"\n[network_identity]\nnetwork_name='raw'", ["10.126.126.1/24"], []));
        Assert.Throws<InvalidOperationException>(() => Validate("dhcp = true", ["10.126.126.1/24"], []));
        Assert.Throws<InvalidOperationException>(() => Validate("ipv4 = \"10.126.127.1/24\"", [""], []));
    }

    [Fact]
    public async Task LinuxArch021_Native_timeout_retains_slot_without_creating_more_workers()
    {
        var boundary = new EasyTierNativeCallBoundary();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => boundary.RunAsync(() =>
            { Interlocked.Increment(ref calls); release.Wait(); return 1; }, TimeSpan.FromMilliseconds(50), default));
            Assert.True(boundary.IsBusy);
            await Assert.ThrowsAsync<InvalidOperationException>(() => boundary.RunAsync(() =>
            { Interlocked.Increment(ref calls); return 2; }, TimeSpan.FromMilliseconds(50), default));
            Assert.Equal(1, calls);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task LinuxArch021_Native_failure_releases_slot_and_cancelled_caller_does_not_start_work()
    {
        var boundary = new EasyTierNativeCallBoundary();
        await Assert.ThrowsAsync<InvalidOperationException>(() => boundary.RunAsync<int>(() => throw new InvalidOperationException("native error"), TimeSpan.FromSeconds(1), default));
        Assert.Equal(42, await boundary.RunAsync(() => 42, TimeSpan.FromSeconds(1), default));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var started = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => boundary.RunAsync(() => { started = true; return 0; }, TimeSpan.FromSeconds(1), cancelled.Token));
        Assert.False(started);
    }
}
