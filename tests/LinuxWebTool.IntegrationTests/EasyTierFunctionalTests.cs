using System.Net;
using System.Text.Json;
using LinuxWebTool.IntegrationTests.Support;
using LinuxWebTool.WebHost.Composition;
using Xunit;

namespace LinuxWebTool.IntegrationTests;

public sealed class EasyTierFunctionalTests
{
    [Fact]
    public async Task EasyTier_EngineStatus_Query_Returns_Success()
    {
        var client = await TestServerFixture.CreateAdminClientAsync();
        var resp = await client.GetAsync("/api/EasyTier/Engine/Status");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var json = await resp.Content.ReadAsStringAsync();
        var status = JsonSerializer.Deserialize<EasyTierEngineStatusDto>(json, AppJsonSerializerContext.Default.Options);
        Assert.NotNull(status);
        Assert.NotEmpty(status.Mode);
        Assert.NotEmpty(status.StorageDirectory);
    }

    [Fact]
    public async Task EasyTier_Nodes_Crud_And_Detail_Flow()
    {
        var client = await TestServerFixture.CreateAdminClientAsync();

        // 1. 获取节点列表
        var listResp = await client.GetAsync("/api/EasyTier/Nodes");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var initialList = JsonSerializer.Deserialize<List<EasyTierNodeStatusDto>>(await listResp.Content.ReadAsStringAsync(), AppJsonSerializerContext.Default.Options);
        Assert.NotNull(initialList);

        // 2. 创建新节点
        var instName = "test_node_" + Guid.NewGuid().ToString("N")[..6];
        var createReq = new CreateEasyTierNodeRequest(
            InstanceName: instName,
            NetworkName: "integration_vnet",
            NetworkSecret: "my_secret_token",
            VirtualIpv4: "10.144.144.10/24",
            EnableDhcp: true,
            Listeners: ["tcp://0.0.0.0:11010", "udp://0.0.0.0:11010"],
            Peers: ["tcp://public.easytier.top:11010"],
            ProxyNetworks: ["192.168.100.0/24"],
            Routes: ["10.0.0.0/8"],
            RawTomlOverride: null,
            AutoStart: false);

        var createJson = JsonSerializer.Serialize(createReq, AppJsonSerializerContext.Default.CreateEasyTierNodeRequest);
        var createResp = await client.PostAsync("/api/EasyTier/Nodes", TestServerFixture.Json(createJson));
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);

        var created = JsonSerializer.Deserialize<EasyTierNodeStatusDto>(await createResp.Content.ReadAsStringAsync(), AppJsonSerializerContext.Default.Options);
        Assert.NotNull(created);
        Assert.Equal(instName, created.InstanceName);
        Assert.Equal("integration_vnet", created.NetworkName);
        Assert.NotNull(created.Listeners);
        Assert.Equal(2, created.Listeners.Count);

        var nodeId = created.Id;

        // 3. 获取单节点详情并验证生成的 TOML
        var detailResp = await client.GetAsync($"/api/EasyTier/Nodes/{nodeId}");
        Assert.Equal(HttpStatusCode.OK, detailResp.StatusCode);
        var detail = JsonSerializer.Deserialize<EasyTierNodeDetailDto>(await detailResp.Content.ReadAsStringAsync(), AppJsonSerializerContext.Default.Options);
        Assert.NotNull(detail);
        Assert.Equal(instName, detail.Config.InstanceName);
        Assert.Contains("integration_vnet", detail.GeneratedToml);
        Assert.Contains(instName, detail.GeneratedToml);
        Assert.Contains("ipv4 = \"10.144.144.10/24\"", detail.GeneratedToml);
        Assert.Contains("dhcp = false", detail.GeneratedToml);
        Assert.Contains("192.168.100.0/24", detail.GeneratedToml);

        // 4. 更新节点配置
        var updateReq = new UpdateEasyTierNodeRequest(
            InstanceName: instName,
            NetworkName: "integration_vnet_renamed",
            NetworkSecret: "updated_secret",
            VirtualIpv4: "10.144.144.12/24",
            EnableDhcp: true,
            Listeners: ["tcp://0.0.0.0:11011"],
            Peers: ["tcp://peer.test.com:11010"],
            ProxyNetworks: ["10.200.0.0/16"],
            Routes: [],
            RawTomlOverride: null,
            AutoStart: false);

        var updateJson = JsonSerializer.Serialize(updateReq, AppJsonSerializerContext.Default.UpdateEasyTierNodeRequest);
        var updateResp = await client.PutAsync($"/api/EasyTier/Nodes/{nodeId}", TestServerFixture.Json(updateJson));
        Assert.Equal(HttpStatusCode.OK, updateResp.StatusCode);

        var updated = JsonSerializer.Deserialize<EasyTierNodeStatusDto>(await updateResp.Content.ReadAsStringAsync(), AppJsonSerializerContext.Default.Options);
        Assert.NotNull(updated);
        var updatedDetailResp = await client.GetAsync($"/api/EasyTier/Nodes/{nodeId}");
        Assert.Equal(HttpStatusCode.OK, updatedDetailResp.StatusCode);
        var updatedDetail = JsonSerializer.Deserialize<EasyTierNodeDetailDto>(await updatedDetailResp.Content.ReadAsStringAsync(), AppJsonSerializerContext.Default.Options);
        Assert.NotNull(updatedDetail);
        Assert.Contains("ipv4 = \"10.144.144.12/24\"", updatedDetail.GeneratedToml);
        Assert.Contains("dhcp = false", updatedDetail.GeneratedToml);
        Assert.Equal("integration_vnet_renamed", updated.NetworkName);

        // 5. 验证删除节点
        var delResp = await client.DeleteAsync($"/api/EasyTier/Nodes/{nodeId}");
        Assert.Equal(HttpStatusCode.OK, delResp.StatusCode);

        var getAfterDel = await client.GetAsync($"/api/EasyTier/Nodes/{nodeId}");
        Assert.Equal(HttpStatusCode.NotFound, getAfterDel.StatusCode);
    }

    [Fact]
    public async Task EasyTier_CreateNode_Duplicate_Name_Rejects()
    {
        var client = await TestServerFixture.CreateAdminClientAsync();
        var instName = "dup_node_" + Guid.NewGuid().ToString("N")[..6];

        var createReq = new CreateEasyTierNodeRequest(
            InstanceName: instName,
            NetworkName: "net_dup",
            NetworkSecret: null,
            VirtualIpv4: null,
            EnableDhcp: true,
            Listeners: null,
            Peers: null,
            ProxyNetworks: null,
            Routes: null,
            RawTomlOverride: null,
            AutoStart: false);

        var createJson = JsonSerializer.Serialize(createReq, AppJsonSerializerContext.Default.CreateEasyTierNodeRequest);
        var resp1 = await client.PostAsync("/api/EasyTier/Nodes", TestServerFixture.Json(createJson));
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);

        // 第二次同名创建应被拒绝
        var resp2 = await client.PostAsync("/api/EasyTier/Nodes", TestServerFixture.Json(createJson));
        Assert.Equal(HttpStatusCode.BadRequest, resp2.StatusCode);
    }

    [Fact]
    public async Task EasyTier_AvailablePort_Probing_Returns_Available_Ports()
    {
        var client = await TestServerFixture.CreateAdminClientAsync();
        var resp = await client.GetAsync("/api/EasyTier/AvailablePort?startPort=11010");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var json = await resp.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<EasyTierAvailablePortDto>(json, AppJsonSerializerContext.Default.Options);
        Assert.NotNull(result);
        Assert.True(result.BasePort >= 1024);
        Assert.Equal(result.BasePort + 1, result.WgPort);
        Assert.NotEmpty(result.SuggestedListeners);
        Assert.Contains($"tcp://0.0.0.0:{result.BasePort}", result.SuggestedListeners);
        Assert.Contains($"udp://0.0.0.0:{result.BasePort}", result.SuggestedListeners);
        Assert.Contains($"wg://0.0.0.0:{result.WgPort}", result.SuggestedListeners);
        Assert.Contains($"tcp://[::]:{result.BasePort}", result.SuggestedListeners);
        Assert.Contains($"udp://[::]:{result.BasePort}", result.SuggestedListeners);
        Assert.Contains($"wg://[::]:{result.WgPort}", result.SuggestedListeners);
    }

    [Fact]
    public async Task EasyTier_Nodes_AdvancedOptions_Roundtrip_Flow()
    {
        var client = await TestServerFixture.CreateAdminClientAsync();
        var instName = "adv_node_" + Guid.NewGuid().ToString("N")[..6];

        // 1. 创建携带全量 P0/P1 扩展选项的节点
        var createReq = new CreateEasyTierNodeRequest(
            InstanceName: instName,
            NetworkName: "adv_vnet",
            NetworkSecret: "adv_secret",
            VirtualIpv4: "10.155.155.1/24",
            EnableDhcp: false,
            Listeners: ["tcp://0.0.0.0:11010"],
            Peers: ["tcp://public.easytier.top:11010"],
            ProxyNetworks: [],
            Routes: [],
            RawTomlOverride: null,
            AutoStart: false,
            RoutingMode: "virtual_only",
            Hostname: "gw-host-01",
            VirtualIpv6: "fd00:beef::1/64",
            MappedListeners: ["tcp://203.0.113.10:11010", "udp://203.0.113.10:11010"],
            ProxyNetworkRules: [new EasyTierProxyNetworkItem("192.168.88.0/24", "10.200.88.0/24")],
            AdvancedConfig: new EasyTierAdvancedConfig(
                DevName: "et_adv0",
                Mtu: 1420,
                EnableIpv6: true,
                LatencyFirst: true,
                BindDevice: true,
                P2pMode: "p2p_only",
                DefaultProtocol: "udp",
                EnableUpnp: false,
                EnableTcpHolePunching: false,
                EnableUdpHolePunching: false,
                EnableSymHolePunching: false));

        var createJson = JsonSerializer.Serialize(createReq, AppJsonSerializerContext.Default.CreateEasyTierNodeRequest);
        var createResp = await client.PostAsync("/api/EasyTier/Nodes", TestServerFixture.Json(createJson));
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);

        var created = JsonSerializer.Deserialize<EasyTierNodeStatusDto>(await createResp.Content.ReadAsStringAsync(), AppJsonSerializerContext.Default.Options);
        Assert.NotNull(created);
        Assert.Equal(instName, created.InstanceName);
        Assert.Equal("gw-host-01", created.Hostname);
        Assert.Equal("fd00:beef::1/64", created.VirtualIpv6);
        Assert.Equal("virtual_only", created.RoutingMode);

        var nodeId = created.Id;

        // 2. 验证详情与生成的 TOML
        var detailResp = await client.GetAsync($"/api/EasyTier/Nodes/{nodeId}");
        Assert.Equal(HttpStatusCode.OK, detailResp.StatusCode);
        var detail = JsonSerializer.Deserialize<EasyTierNodeDetailDto>(await detailResp.Content.ReadAsStringAsync(), AppJsonSerializerContext.Default.Options);
        Assert.NotNull(detail);
        Assert.Equal("gw-host-01", detail.Config.Hostname);
        Assert.Equal("fd00:beef::1/64", detail.Config.VirtualIpv6);
        Assert.Equal("virtual_only", detail.Config.RoutingMode);
        Assert.NotNull(detail.Config.ProxyNetworkRules);
        Assert.Single(detail.Config.ProxyNetworkRules);
        Assert.Equal("192.168.88.0/24", detail.Config.ProxyNetworkRules[0].Cidr);
        Assert.Equal("10.200.88.0/24", detail.Config.ProxyNetworkRules[0].MappedCidr);
        Assert.NotNull(detail.Config.AdvancedConfig);
        Assert.Equal("et_adv0", detail.Config.AdvancedConfig.DevName);
        Assert.Equal(1420, detail.Config.AdvancedConfig.Mtu);
        Assert.True(detail.Config.AdvancedConfig.BindDevice);
        Assert.Equal("p2p_only", detail.Config.AdvancedConfig.P2pMode);
        Assert.False(detail.Config.AdvancedConfig.EnableUpnp);

        // 验证 TOML 中严格遵循顶层结构输出与子网映射
        Assert.Contains("hostname = \"gw-host-01\"", detail.GeneratedToml);
        Assert.Contains("ipv6 = \"fd00:beef::1/64\"", detail.GeneratedToml);
        Assert.Contains("routes = []", detail.GeneratedToml);
        Assert.Contains("mapped_listeners = [\"tcp://203.0.113.10:11010\", \"udp://203.0.113.10:11010\"]", detail.GeneratedToml);
        Assert.Contains("[[proxy_network]]", detail.GeneratedToml);
        Assert.Contains("cidr = \"192.168.88.0/24\"", detail.GeneratedToml);
        Assert.Contains("mapped_cidr = \"10.200.88.0/24\"", detail.GeneratedToml);
        Assert.Contains("[flags]", detail.GeneratedToml);
        Assert.Contains("dev_name = \"et_adv0\"", detail.GeneratedToml);
        Assert.Contains("mtu = 1420", detail.GeneratedToml);
        Assert.Contains("bind_device = true", detail.GeneratedToml);
        Assert.Contains("p2p_only = true", detail.GeneratedToml);
        Assert.Contains("disable_upnp = true", detail.GeneratedToml);

        // 3. 更新为 manual 路由模式并调整高级配置
        var updateReq = new UpdateEasyTierNodeRequest(
            InstanceName: instName,
            NetworkName: "adv_vnet",
            NetworkSecret: "adv_secret",
            VirtualIpv4: "10.155.155.1/24",
            EnableDhcp: false,
            Listeners: ["tcp://0.0.0.0:11010"],
            Peers: ["tcp://public.easytier.top:11010"],
            ProxyNetworks: [],
            Routes: ["10.50.0.0/16"],
            RawTomlOverride: null,
            AutoStart: false,
            RoutingMode: "manual",
            Hostname: "gw-host-01-updated",
            VirtualIpv6: "fd00:beef::2/64",
            MappedListeners: [],
            ProxyNetworkRules: [new EasyTierProxyNetworkItem("192.168.99.0/24", null)],
            AdvancedConfig: new EasyTierAdvancedConfig(
                DevName: "et_adv1",
                Mtu: 1400,
                EnableIpv6: false,
                LatencyFirst: false,
                BindDevice: false,
                P2pMode: "need_p2p",
                DefaultProtocol: "tcp",
                EnableUpnp: true,
                EnableTcpHolePunching: true,
                EnableUdpHolePunching: true,
                EnableSymHolePunching: true));

        var updateJson = JsonSerializer.Serialize(updateReq, AppJsonSerializerContext.Default.UpdateEasyTierNodeRequest);
        var updateResp = await client.PutAsync($"/api/EasyTier/Nodes/{nodeId}", TestServerFixture.Json(updateJson));
        Assert.Equal(HttpStatusCode.OK, updateResp.StatusCode);

        var updatedDetailResp = await client.GetAsync($"/api/EasyTier/Nodes/{nodeId}");
        Assert.Equal(HttpStatusCode.OK, updatedDetailResp.StatusCode);
        var updatedDetail = JsonSerializer.Deserialize<EasyTierNodeDetailDto>(await updatedDetailResp.Content.ReadAsStringAsync(), AppJsonSerializerContext.Default.Options);
        Assert.NotNull(updatedDetail);
        Assert.Equal("gw-host-01-updated", updatedDetail.Config.Hostname);
        Assert.Equal("manual", updatedDetail.Config.RoutingMode);
        Assert.Contains("routes = [\"10.50.0.0/16\"]", updatedDetail.GeneratedToml);
        Assert.Contains("need_p2p = true", updatedDetail.GeneratedToml);
        Assert.Contains("cidr = \"192.168.99.0/24\"", updatedDetail.GeneratedToml);
        Assert.DoesNotContain("mapped_cidr =", updatedDetail.GeneratedToml);

        // 4. 清理删除节点
        var delResp = await client.DeleteAsync($"/api/EasyTier/Nodes/{nodeId}");
        Assert.Equal(HttpStatusCode.OK, delResp.StatusCode);
    }
}
