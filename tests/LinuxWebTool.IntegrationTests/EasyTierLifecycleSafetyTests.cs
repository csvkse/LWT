using System.Reflection;
using LinuxWebTool.Infrastructure.Features.EasyTier.Adapters;
using LinuxWebTool.Infrastructure.Shared.Persistence;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using LinuxWebTool.IntegrationTests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinuxWebTool.IntegrationTests;

public sealed class EasyTierLifecycleSafetyTests
{
    private static void Seed(EasyTierNodeManager manager, string id, string name, string network)
    {
        var type = typeof(EasyTierNodeManager).GetNestedType("NodeRuntimeState", BindingFlags.NonPublic)!;
        var state = Activator.CreateInstance(type, true)!;
        type.GetProperty("InstanceName")!.SetValue(state, name);
        type.GetProperty("Network")!.SetValue(state, network);
        type.GetProperty("Mode")!.SetValue(state, EasyTierEngineMode.NativeFfi);
        var map = typeof(EasyTierNodeManager).GetField("_runtimeStates", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!;
        map.GetType().GetMethod("TryAdd")!.Invoke(map, [id, state]);
    }

    private sealed class RecordingManager(EasyTierNodeStore store, EasyTierHostSupervisor supervisor)
        : EasyTierNodeManager(store, supervisor, NullLogger<EasyTierNodeManager>.Instance)
    {
        public string? StoppedName;
        public bool FailStop;
        public int Starts;
        protected override Task StopNodeInternalAsync(string id, string name, CancellationToken ct)
        {
            StoppedName = name;
            if (FailStop) throw new InvalidOperationException("stop failed");
            return Task.CompletedTask;
        }
        protected override Task<bool> StartNodeInternalAsync(EasyTierNodeEntity node, CancellationToken ct)
        {
            Starts++;
            return Task.FromResult(true);
        }
    }

    private sealed class ConcurrentManager(EasyTierNodeStore store, EasyTierHostSupervisor supervisor)
        : EasyTierNodeManager(store, supervisor, NullLogger<EasyTierNodeManager>.Instance)
    {
        private int active;
        public int Maximum;
        protected override async Task<bool> StartNodeInternalAsync(EasyTierNodeEntity node, CancellationToken ct)
        {
            var count = Interlocked.Increment(ref active);
            Maximum = Math.Max(Maximum, count);
            try { await Task.Delay(50, ct); return true; }
            finally { Interlocked.Decrement(ref active); }
        }
    }

    private sealed class BlockedSnapshotManager(EasyTierNodeStore store, EasyTierHostSupervisor supervisor,
        ManualResetEventSlim entered, ManualResetEventSlim release)
        : EasyTierNodeManager(store, supervisor, NullLogger<EasyTierNodeManager>.Instance)
    {
        public int Calls;
        protected override Dictionary<string, string> CollectNetworkInfos()
        {
            Interlocked.Increment(ref Calls);
            entered.Set();
            release.Wait();
            return [];
        }
    }

    [Fact]
    public async Task LinuxArch021_Blocked_native_snapshot_does_not_accumulate_status_requests()
    {
        var services = (await TestServerFixture.GetAppAsync()).Services;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var manager = new BlockedSnapshotManager(services.GetRequiredService<EasyTierNodeStore>(),
            services.GetRequiredService<EasyTierHostSupervisor>(), entered, release);
        using var cancellation = new CancellationTokenSource();
        var first = manager.GetAllNodeStatusesAsync(cancellation.Token);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            await manager.GetAllNodeStatusesAsync().WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(1, manager.Calls);
            // Other APIs remain reachable while the native worker is still blocked.
            using var client = await TestServerFixture.CreateAnonymousClientAsync();
            using var response = await client.PostAsync("/api/Auth/Login", TestServerFixture.Json(
                "{\"userName\":\"admin\",\"password\":\"integration-test-password\"}")).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            release.Set();
            var boundary = typeof(EasyTierNodeManager).GetField("NativeCalls", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var active = (Task)boundary.GetType().GetField("active", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(boundary)!;
            await active.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task LinuxArch022_Check_and_start_are_serialized_across_nodes()
    {
        var services = (await TestServerFixture.GetAppAsync()).Services;
        var store = services.GetRequiredService<EasyTierNodeStore>();
        var one = Node("serialized-one");
        var two = Node("serialized-two");
        await store.UpsertNodeAsync(one);
        await store.UpsertNodeAsync(two);
        var manager = new ConcurrentManager(store, services.GetRequiredService<EasyTierHostSupervisor>());
        try
        {
            await Task.WhenAll(manager.StartNodeAsync(one.Id), manager.StartNodeAsync(two.Id));
            Assert.Equal(1, manager.Maximum);
        }
        finally { await store.DeleteNodeAsync(one.Id); await store.DeleteNodeAsync(two.Id); }
    }

    private static EasyTierNodeEntity Node(string name) => new()
    {
        Id = Guid.NewGuid().ToString("N"), InstanceName = name + Guid.NewGuid().ToString("N")[..6],
        NetworkName = "lifecycle-test", VirtualIpv4 = "198.18.254.2/24", EnableDhcp = false,
        ListenersJson = "[]", PeersJson = "[]", RoutesJson = "[]", ProxyNetworksJson = "[]",
    };

    [Fact]
    public async Task LinuxArch022_Concurrent_duplicate_start_is_idempotent()
    {
        var services = (await TestServerFixture.GetAppAsync()).Services;
        var store = services.GetRequiredService<EasyTierNodeStore>();
        var node = Node("already-running");
        await store.UpsertNodeAsync(node);
        var manager = new EasyTierNodeManager(store, services.GetRequiredService<EasyTierHostSupervisor>(), NullLogger<EasyTierNodeManager>.Instance);
        Seed(manager, node.Id, node.InstanceName, node.VirtualIpv4!);
        try
        {
            var results = await Task.WhenAll(manager.StartNodeAsync(node.Id), manager.StartNodeAsync(node.Id));
            Assert.All(results, Assert.True); // No engine is installed: both must reuse the running record.
        }
        finally { await store.DeleteNodeAsync(node.Id); }
    }

    [Fact]
    public async Task LinuxArch022_Conflict_is_rejected_before_engine_launch()
    {
        var services = (await TestServerFixture.GetAppAsync()).Services;
        var store = services.GetRequiredService<EasyTierNodeStore>();
        var node = Node("overlap");
        await store.UpsertNodeAsync(node);
        var manager = new EasyTierNodeManager(store, services.GetRequiredService<EasyTierHostSupervisor>(), NullLogger<EasyTierNodeManager>.Instance);
        Seed(manager, "other-id", "other", "198.18.254.1/24");
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartNodeAsync(node.Id));
            Assert.Contains("重叠", error.Message);
        }
        finally { await store.DeleteNodeAsync(node.Id); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinuxArch022_Rename_stops_old_name_and_failed_stop_prevents_restart(bool failStop)
    {
        var services = (await TestServerFixture.GetAppAsync()).Services;
        var store = services.GetRequiredService<EasyTierNodeStore>();
        var node = Node("old-name");
        await store.UpsertNodeAsync(node);
        var manager = new RecordingManager(store, services.GetRequiredService<EasyTierHostSupervisor>()) { FailStop = failStop };
        Seed(manager, node.Id, node.InstanceName, node.VirtualIpv4!);
        var request = new UpdateEasyTierNodeRequest(node.InstanceName + "-new", node.NetworkName, null,
            node.VirtualIpv4, false, [], [], [], [], null, false);
        try
        {
            if (failStop)
                await Assert.ThrowsAsync<InvalidOperationException>(() => manager.UpdateNodeAsync(node.Id, request));
            else
                await manager.UpdateNodeAsync(node.Id, request);
            Assert.Equal(node.InstanceName, manager.StoppedName);
            Assert.Equal(failStop ? 0 : 1, manager.Starts);
            if (failStop)
            {
                var map = typeof(EasyTierNodeManager).GetField("_runtimeStates", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!;
                Assert.True((bool)map.GetType().GetMethod("ContainsKey")!.Invoke(map, [node.Id])!);
            }
        }
        finally { await store.DeleteNodeAsync(node.Id); }
    }
}
