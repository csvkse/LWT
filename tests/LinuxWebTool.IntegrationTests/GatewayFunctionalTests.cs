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

    [Fact]
    public async Task Gateway_Website_Does_Not_Break_Admin_Dashboard_And_Static_Files()
    {
        var client = await TestServerFixture.CreateAdminClientAsync();

        // 1. 创建并启用一个网站代理 (例如 plex)
        var createReq = new SaveGatewayWebsiteRequest(
            Name: "plex-isolated-test",
            TargetUrl: "http://192.168.1.60:32400",
            RewriteBody: true,
            RewriteCookie: true,
            IsEnabled: true);

        var createJson = JsonSerializer.Serialize(createReq, AppJsonSerializerContext.Default.SaveGatewayWebsiteRequest);
        var createResp = await client.PostAsync("/api/Gateway/Websites", TestServerFixture.Json(createJson));
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);

        try
        {
            // 2. 验证管理后台入口与静态文件均可正常访问（绝不能被 YARP 劫持返回 400）
            var healthResp = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, healthResp.StatusCode);

            var appHtmlResp = await client.GetAsync("/app/");
            Assert.Equal(HttpStatusCode.OK, appHtmlResp.StatusCode);

            var mainJsResp = await client.GetAsync("/app/main.js");
            Assert.Equal(HttpStatusCode.OK, mainJsResp.StatusCode);

            var styleCssResp = await client.GetAsync("/app/style.css");
            Assert.Equal(HttpStatusCode.OK, styleCssResp.StatusCode);

            // 3. 验证管理 API 正常响应
            var checkResp = await client.GetAsync("/api/Auth/Check");
            Assert.Equal(HttpStatusCode.OK, checkResp.StatusCode);

            var listResp = await client.GetAsync("/api/Gateway/Websites");
            Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        }
        finally
        {
            // 清理测试网站
            var listResp = await client.GetAsync("/api/Gateway/Websites");
            if (listResp.IsSuccessStatusCode)
            {
                var list = JsonSerializer.Deserialize<List<GatewayWebsiteItem>>(await listResp.Content.ReadAsStringAsync(), AppJsonSerializerContext.Default.Options);
                var testItem = list?.FirstOrDefault(w => w.Name == "plex-isolated-test");
                if (testItem != null)
                {
                    await client.DeleteAsync($"/api/Gateway/Websites/{testItem.Id}");
                }
            }
        }
    }

    [Fact]
    public async Task Gateway_Input_Validation_Rejects_Invalid_Requests()
    {
        var client = await TestServerFixture.CreateAdminClientAsync();

        // 1. 路由参数校验
        var badRoute = new SaveGatewayRouteRequest("", "cluster-1", "/path", null, null, null, 0, true);
        var rResp = await client.PostAsync("/api/Gateway/Routes", TestServerFixture.Json(JsonSerializer.Serialize(badRoute, AppJsonSerializerContext.Default.SaveGatewayRouteRequest)));
        Assert.Equal(HttpStatusCode.BadRequest, rResp.StatusCode);

        // 2. 集群参数校验
        var badCluster = new SaveGatewayClusterRequest("", "RoundRobin", "[]", null);
        var cResp = await client.PostAsync("/api/Gateway/Clusters", TestServerFixture.Json(JsonSerializer.Serialize(badCluster, AppJsonSerializerContext.Default.SaveGatewayClusterRequest)));
        Assert.Equal(HttpStatusCode.BadRequest, cResp.StatusCode);

        // 3. 网站代理参数校验
        var badWebsite = new SaveGatewayWebsiteRequest("", "", true, true, true);
        var wResp = await client.PostAsync("/api/Gateway/Websites", TestServerFixture.Json(JsonSerializer.Serialize(badWebsite, AppJsonSerializerContext.Default.SaveGatewayWebsiteRequest)));
        Assert.Equal(HttpStatusCode.BadRequest, wResp.StatusCode);

        // 4. 端口转发参数校验：非法协议
        var badProto = new SaveGatewayTcpRouteRequest("test", "FTP", 8080, "127.0.0.1", 80, true);
        var pResp = await client.PostAsync("/api/Gateway/TcpRoutes", TestServerFixture.Json(JsonSerializer.Serialize(badProto, AppJsonSerializerContext.Default.SaveGatewayTcpRouteRequest)));
        Assert.Equal(HttpStatusCode.BadRequest, pResp.StatusCode);

        // 5. 端口转发参数校验：非法端口号
        var badPort = new SaveGatewayTcpRouteRequest("test", "TCP", 70000, "127.0.0.1", 80, true);
        var portResp = await client.PostAsync("/api/Gateway/TcpRoutes", TestServerFixture.Json(JsonSerializer.Serialize(badPort, AppJsonSerializerContext.Default.SaveGatewayTcpRouteRequest)));
        Assert.Equal(HttpStatusCode.BadRequest, portResp.StatusCode);
    }

    [Fact]
    public async Task Gateway_Udp_Route_Crud_And_Reload()
    {
        var client = await TestServerFixture.CreateAdminClientAsync();

        // 1. 创建 UDP 转发规则
        var createReq = new SaveGatewayTcpRouteRequest(
            Name: "dns-lan",
            Protocol: "UDP",
            ListenPort: 19053,
            ForwardHost: "192.168.1.1",
            ForwardPort: 53,
            IsEnabled: true);

        var createJson = JsonSerializer.Serialize(createReq, AppJsonSerializerContext.Default.SaveGatewayTcpRouteRequest);
        var createResp = await client.PostAsync("/api/Gateway/TcpRoutes", TestServerFixture.Json(createJson));
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);

        try
        {
            // 2. 查询列表验证 UDP 规则创建成功
            var listResp = await client.GetAsync("/api/Gateway/TcpRoutes");
            Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
            var tcps = JsonSerializer.Deserialize<List<GatewayTcpRouteItem>>(await listResp.Content.ReadAsStringAsync(), AppJsonSerializerContext.Default.Options)!;
            var found = tcps.FirstOrDefault(t => t.Name == "dns-lan");
            Assert.NotNull(found);
            Assert.Equal("UDP", found.Protocol);
            Assert.Equal(19053, found.ListenPort);
            Assert.Equal("192.168.1.1", found.ForwardHost);
            Assert.Equal(53, found.ForwardPort);

            // 3. 更新 UDP 规则
            var updateReq = new SaveGatewayTcpRouteRequest(
                Name: "dns-lan-updated",
                Protocol: "UDP",
                ListenPort: 19053,
                ForwardHost: "192.168.1.2",
                ForwardPort: 5353,
                IsEnabled: true);
            var updateResp = await client.PutAsync($"/api/Gateway/TcpRoutes/{found.Id}", TestServerFixture.Json(JsonSerializer.Serialize(updateReq, AppJsonSerializerContext.Default.SaveGatewayTcpRouteRequest)));
            Assert.Equal(HttpStatusCode.OK, updateResp.StatusCode);

            // 4. 删除 UDP 规则
            var deleteResp = await client.DeleteAsync($"/api/Gateway/TcpRoutes/{found.Id}");
            Assert.Equal(HttpStatusCode.OK, deleteResp.StatusCode);
        }
        finally
        {
            var listResp = await client.GetAsync("/api/Gateway/TcpRoutes");
            if (listResp.IsSuccessStatusCode)
            {
                var list = JsonSerializer.Deserialize<List<GatewayTcpRouteItem>>(await listResp.Content.ReadAsStringAsync(), AppJsonSerializerContext.Default.Options);
                var testItem = list?.FirstOrDefault(t => t.Name == "dns-lan" || t.Name == "dns-lan-updated");
                if (testItem != null)
                {
                    await client.DeleteAsync($"/api/Gateway/TcpRoutes/{testItem.Id}");
                }
            }
        }
    }

    [Fact]
    public async Task Gateway_Website_Proxy_Access_And_Ssrf_Protection()
    {
        var client = await TestServerFixture.CreateAdminClientAsync();

        // 1. 注册合法站点
        var createReq = new SaveGatewayWebsiteRequest(
            Name: "ha-local",
            TargetUrl: "http://127.0.0.1:48123",
            RewriteBody: true,
            RewriteCookie: true,
            IsEnabled: true);

        var createJson = JsonSerializer.Serialize(createReq, AppJsonSerializerContext.Default.SaveGatewayWebsiteRequest);
        var createResp = await client.PostAsync("/api/Gateway/Websites", TestServerFixture.Json(createJson));
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);

        try
        {
            // 2. 访问未在白名单中的目标：必须返回 403 Forbidden（防御 SSRF 开放代理探测）
            var unauthProxy = await client.GetAsync("/proxy/evil.attacker.com/steal");
            Assert.Equal(HttpStatusCode.Forbidden, unauthProxy.StatusCode);

            var unauthAlias = await client.GetAsync("/s/not-exist-alias/");
            Assert.Equal(HttpStatusCode.Forbidden, unauthAlias.StatusCode);

            var unauthScheme = await client.GetAsync("/http:/evil.internal.corp:8080/");
            Assert.Equal(HttpStatusCode.Forbidden, unauthScheme.StatusCode);

            // 3. 访问已在白名单中的站点：绝不应返回 403 Forbidden
            // （白名单放行通过；由于 127.0.0.1:48123 未实际启动，YARP 会返回 503/502，这证明路由与白名单校验完全通过且未被 403/400 阻断）
            var authProxy = await client.GetAsync("/proxy/127.0.0.1:48123/");
            Assert.NotEqual(HttpStatusCode.Forbidden, authProxy.StatusCode);
            Assert.NotEqual(HttpStatusCode.BadRequest, authProxy.StatusCode);

            var authAlias = await client.GetAsync("/proxy/ha-local/");
            Assert.NotEqual(HttpStatusCode.Forbidden, authAlias.StatusCode);
            Assert.NotEqual(HttpStatusCode.BadRequest, authAlias.StatusCode);

            var authS = await client.GetAsync("/s/ha-local/");
            Assert.NotEqual(HttpStatusCode.Forbidden, authS.StatusCode);
            Assert.NotEqual(HttpStatusCode.BadRequest, authS.StatusCode);

            var authScheme = await client.GetAsync("/http:/127.0.0.1:48123/");
            Assert.NotEqual(HttpStatusCode.Forbidden, authScheme.StatusCode);
            Assert.NotEqual(HttpStatusCode.BadRequest, authScheme.StatusCode);
        }
        finally
        {
            var listResp = await client.GetAsync("/api/Gateway/Websites");
            if (listResp.IsSuccessStatusCode)
            {
                var list = JsonSerializer.Deserialize<List<GatewayWebsiteItem>>(await listResp.Content.ReadAsStringAsync(), AppJsonSerializerContext.Default.Options);
                var testItem = list?.FirstOrDefault(w => w.Name == "ha-local");
                if (testItem != null)
                {
                    await client.DeleteAsync($"/api/Gateway/Websites/{testItem.Id}");
                }
            }
        }
    }
}

