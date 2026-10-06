using System.Net;
using System.Text.Json;
using LinuxWebTool.Contracts.Models;
using LinuxWebTool.IntegrationTests.Support;
using LinuxWebTool.WebHost.Composition;
using Xunit;

namespace LinuxWebTool.IntegrationTests;

public sealed class GatewayFunctionalTests
{
    [Fact]
    public async Task Gateway_Routes_Crud_Api()
    {
        var client = await TestServerFixture.CreateAdminClientAsync();

        // 1. 创建路由
        var createReq = new SaveGatewayRouteRequest(
            RouteId: "route-test-1",
            ClusterId: "cluster-test-1",
            MatchPath: "/service/test/{**catch-all}",
            MatchHosts: "test.lan",
            Transforms: null,
            Metadata: null,
            OrderNum: 10,
            IsEnabled: true);

        var createJson = JsonSerializer.Serialize(createReq, AppJsonSerializerContext.Default.SaveGatewayRouteRequest);
        var createResp = await client.PostAsync("/api/Gateway/Routes", TestServerFixture.Json(createJson));
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);

        // 2. 查询路由列表
        var listResp = await client.GetAsync("/api/Gateway/Routes");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var listBody = await listResp.Content.ReadAsStringAsync();
        var routes = JsonSerializer.Deserialize<List<GatewayRouteItem>>(listBody, AppJsonSerializerContext.Default.Options)!;
        var found = routes.FirstOrDefault(r => r.RouteId == "route-test-1");
        Assert.NotNull(found);
        Assert.Equal("cluster-test-1", found.ClusterId);
        Assert.Equal("/service/test/{**catch-all}", found.MatchPath);

        // 3. 更新路由
        var updateReq = new SaveGatewayRouteRequest(
            RouteId: "route-test-1",
            ClusterId: "cluster-test-1",
            MatchPath: "/service/updated/{**catch-all}",
            MatchHosts: "test.lan",
            Transforms: null,
            Metadata: null,
            OrderNum: 20,
            IsEnabled: true);

        var updateJson = JsonSerializer.Serialize(updateReq, AppJsonSerializerContext.Default.SaveGatewayRouteRequest);
        var updateResp = await client.PutAsync($"/api/Gateway/Routes/{found.Id}", TestServerFixture.Json(updateJson));
        Assert.Equal(HttpStatusCode.OK, updateResp.StatusCode);

        // 4. 删除路由
        var deleteResp = await client.DeleteAsync($"/api/Gateway/Routes/{found.Id}");
        Assert.Equal(HttpStatusCode.OK, deleteResp.StatusCode);

        // 5. 确认删除成功
        var afterDeleteResp = await client.GetAsync("/api/Gateway/Routes");
        var afterRoutes = JsonSerializer.Deserialize<List<GatewayRouteItem>>(await afterDeleteResp.Content.ReadAsStringAsync(), AppJsonSerializerContext.Default.Options)!;
        Assert.DoesNotContain(afterRoutes, r => r.Id == found.Id);
    }

    [Fact]
    public async Task Gateway_Clusters_Crud_Api()
    {
        var client = await TestServerFixture.CreateAdminClientAsync();

        // 1. 创建集群
        var createReq = new SaveGatewayClusterRequest(
            ClusterId: "cluster-alpha",
            LoadBalancingPolicy: "RoundRobin",
            Destinations: "[{\"Address\":\"http://10.0.0.10:8080\"}]",
            HealthCheckConfig: null);

        var createJson = JsonSerializer.Serialize(createReq, AppJsonSerializerContext.Default.SaveGatewayClusterRequest);
        var createResp = await client.PostAsync("/api/Gateway/Clusters", TestServerFixture.Json(createJson));
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);

        // 2. 查询集群列表
        var listResp = await client.GetAsync("/api/Gateway/Clusters");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var clusters = JsonSerializer.Deserialize<List<GatewayClusterItem>>(await listResp.Content.ReadAsStringAsync(), AppJsonSerializerContext.Default.Options)!;
        var found = clusters.FirstOrDefault(c => c.ClusterId == "cluster-alpha");
        Assert.NotNull(found);
        Assert.Equal("RoundRobin", found.LoadBalancingPolicy);

        // 3. 更新集群
        var updateReq = new SaveGatewayClusterRequest(
            ClusterId: "cluster-alpha",
            LoadBalancingPolicy: "Random",
            Destinations: "[{\"Address\":\"http://10.0.0.20:8080\"}]",
            HealthCheckConfig: null);

        var updateJson = JsonSerializer.Serialize(updateReq, AppJsonSerializerContext.Default.SaveGatewayClusterRequest);
        var updateResp = await client.PutAsync($"/api/Gateway/Clusters/{found.Id}", TestServerFixture.Json(updateJson));
        Assert.Equal(HttpStatusCode.OK, updateResp.StatusCode);

        // 4. 删除集群
        var deleteResp = await client.DeleteAsync($"/api/Gateway/Clusters/{found.Id}");
        Assert.Equal(HttpStatusCode.OK, deleteResp.StatusCode);

        var afterList = JsonSerializer.Deserialize<List<GatewayClusterItem>>(await (await client.GetAsync("/api/Gateway/Clusters")).Content.ReadAsStringAsync(), AppJsonSerializerContext.Default.Options)!;
        Assert.DoesNotContain(afterList, c => c.Id == found.Id);
    }

    [Fact]
    public async Task Gateway_Websites_Crud_Api()
    {
        var client = await TestServerFixture.CreateAdminClientAsync();

        // 1. 创建网站代理
        var createReq = new SaveGatewayWebsiteRequest(
            Name: "openwrt",
            TargetUrl: "http://192.168.1.1:80",
            RewriteBody: true,
            RewriteCookie: true,
            IsEnabled: true);

        var createJson = JsonSerializer.Serialize(createReq, AppJsonSerializerContext.Default.SaveGatewayWebsiteRequest);
        var createResp = await client.PostAsync("/api/Gateway/Websites", TestServerFixture.Json(createJson));
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);

        // 2. 查询网站代理
        var listResp = await client.GetAsync("/api/Gateway/Websites");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var websites = JsonSerializer.Deserialize<List<GatewayWebsiteItem>>(await listResp.Content.ReadAsStringAsync(), AppJsonSerializerContext.Default.Options)!;
        var found = websites.FirstOrDefault(w => w.Name == "openwrt");
        Assert.NotNull(found);
        Assert.Equal("http://192.168.1.1:80", found.TargetUrl);
        Assert.True(found.RewriteBody);

        // 3. 更新网站代理
        var updateReq = new SaveGatewayWebsiteRequest(
            Name: "openwrt-main",
            TargetUrl: "https://192.168.1.1:443",
            RewriteBody: false,
            RewriteCookie: true,
            IsEnabled: true);

        var updateJson = JsonSerializer.Serialize(updateReq, AppJsonSerializerContext.Default.SaveGatewayWebsiteRequest);
        var updateResp = await client.PutAsync($"/api/Gateway/Websites/{found.Id}", TestServerFixture.Json(updateJson));
        Assert.Equal(HttpStatusCode.OK, updateResp.StatusCode);

        // 4. 删除网站代理
        var deleteResp = await client.DeleteAsync($"/api/Gateway/Websites/{found.Id}");
        Assert.Equal(HttpStatusCode.OK, deleteResp.StatusCode);

        var afterList = JsonSerializer.Deserialize<List<GatewayWebsiteItem>>(await (await client.GetAsync("/api/Gateway/Websites")).Content.ReadAsStringAsync(), AppJsonSerializerContext.Default.Options)!;
        Assert.DoesNotContain(afterList, w => w.Id == found.Id);
    }

    [Fact]
    public async Task Gateway_TcpRoutes_Crud_Api()
    {
        var client = await TestServerFixture.CreateAdminClientAsync();

        // 1. 创建 TCP 转发规则
        var createReq = new SaveGatewayTcpRouteRequest(
            Name: "ssh-nas",
            Protocol: "TCP",
            ListenPort: 12222,
            ForwardHost: "192.168.1.100",
            ForwardPort: 22,
            IsEnabled: true);

        var createJson = JsonSerializer.Serialize(createReq, AppJsonSerializerContext.Default.SaveGatewayTcpRouteRequest);
        var createResp = await client.PostAsync("/api/Gateway/TcpRoutes", TestServerFixture.Json(createJson));
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);

        // 2. 查询 TCP 转发规则
        var listResp = await client.GetAsync("/api/Gateway/TcpRoutes");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var tcps = JsonSerializer.Deserialize<List<GatewayTcpRouteItem>>(await listResp.Content.ReadAsStringAsync(), AppJsonSerializerContext.Default.Options)!;
        var found = tcps.FirstOrDefault(t => t.Name == "ssh-nas");
        Assert.NotNull(found);
        Assert.Equal(12222, found.ListenPort);
        Assert.Equal("192.168.1.100", found.ForwardHost);
        Assert.Equal(22, found.ForwardPort);

        // 3. 更新 TCP 转发规则
        var updateReq = new SaveGatewayTcpRouteRequest(
            Name: "ssh-nas-updated",
            Protocol: "TCP",
            ListenPort: 12222,
            ForwardHost: "192.168.1.101",
            ForwardPort: 2222,
            IsEnabled: false);

        var updateJson = JsonSerializer.Serialize(updateReq, AppJsonSerializerContext.Default.SaveGatewayTcpRouteRequest);
        var updateResp = await client.PutAsync($"/api/Gateway/TcpRoutes/{found.Id}", TestServerFixture.Json(updateJson));
        Assert.Equal(HttpStatusCode.OK, updateResp.StatusCode);

        // 4. 删除 TCP 转发规则
        var deleteResp = await client.DeleteAsync($"/api/Gateway/TcpRoutes/{found.Id}");
        Assert.Equal(HttpStatusCode.OK, deleteResp.StatusCode);

        var afterList = JsonSerializer.Deserialize<List<GatewayTcpRouteItem>>(await (await client.GetAsync("/api/Gateway/TcpRoutes")).Content.ReadAsStringAsync(), AppJsonSerializerContext.Default.Options)!;
        Assert.DoesNotContain(afterList, t => t.Id == found.Id);
    }
}
