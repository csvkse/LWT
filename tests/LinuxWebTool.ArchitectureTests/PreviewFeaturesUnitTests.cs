using LinuxWebTool.Contracts.Models;
using LinuxWebTool.Infrastructure.Persistence;
using LinuxWebTool.Infrastructure.Persistence.Entities;
using LinuxWebTool.Infrastructure.Security;
using LinuxWebTool.Infrastructure.Tunnel;
using LinuxWebTool.WebHost.Gateway;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinuxWebTool.ArchitectureTests;

public sealed class PreviewFeaturesUnitTests
{
    [Fact]
    public async Task ApiKeyService_Create_Validate_And_Invalidate_Works()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"lwt_test_keys_{Guid.NewGuid():N}.db");
        try
        {
            var dbFactory = DbSetup.CreateFactory($"Data Source={tempDb}");
            DbSetup.Initialize(dbFactory);

            var store = new ApiKeyStore(dbFactory);
            var service = new ApiKeyService(store, NullLogger<ApiKeyService>.Instance);

            var req = new CreateApiKeyRequest(
                Name: "Test MCP Agent",
                AllowApi: true,
                AllowMcp: true,
                AllowTerminal: true,
                AllowSchedules: false,
                AllowFiles: true,
                AllowTranscode: false,
                AllowGateway: false);

            var (rawKey, entity) = await service.CreateAsync(req);

            Assert.StartsWith("lwt_live_", rawKey);
            Assert.Equal("Test MCP Agent", entity.Name);
            Assert.True(entity.AllowMcp);
            Assert.False(entity.AllowSchedules);

            // 首次校验（走数据库并回填缓存）
            var validated = await service.ValidateAsync(rawKey);
            Assert.NotNull(validated);
            Assert.Equal(entity.Id, validated.Id);

            // 第二次校验（走内存缓存）
            var cached = await service.ValidateAsync(rawKey);
            Assert.NotNull(cached);
            Assert.Equal(entity.Id, cached.Id);

            // 错误密钥校验失败
            var invalid = await service.ValidateAsync("lwt_live_wrong_key_12345678");
            Assert.Null(invalid);

            // 缓存失效
            service.InvalidateCache(entity.KeyHash);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }

    [Theory]
    [InlineData("Example.com:80", "example.com")]
    [InlineData("Example.com:443", "example.com")]
    [InlineData("192.168.1.1:8080", "192.168.1.1:8080")]
    [InlineData("[::1]:80", "[::1]")]
    [InlineData("[::1]:8080", "[::1]:8080")]
    public void WebsiteProxy_NormalizeAuthority_Handles_Default_Ports_And_Casing(string input, string expected)
    {
        var normalized = WebsiteProxyTransformProvider.NormalizeAuthority(input);
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void WebsiteAllowList_Matches_By_Authority_And_Alias()
    {
        var allowList = new WebsiteAllowList();
        var websites = new List<GatewayWebsiteEntity>
        {
            new()
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "router",
                TargetUrl = "http://192.168.1.1:80",
                RewriteBody = true,
                RewriteCookie = true,
                IsEnabled = true
            },
            new()
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "nas",
                TargetUrl = "https://nas.local:5001",
                RewriteBody = false,
                RewriteCookie = true,
                IsEnabled = true
            }
        };

        allowList.Replace(websites);

        Assert.Equal(2, allowList.Count);

        // 匹配 authority
        Assert.True(allowList.TryGetByAuthority("192.168.1.1", out var routerEntry));
        Assert.NotNull(routerEntry);
        Assert.Equal("http", routerEntry.UpstreamScheme);
        Assert.True(routerEntry.RewriteBody);

        // 匹配 alias
        Assert.True(allowList.TryGetByAlias("nas", out var nasEntry));
        Assert.NotNull(nasEntry);
        Assert.Equal("https", nasEntry.UpstreamScheme);
        Assert.False(nasEntry.RewriteBody);

        // 未知拒绝
        Assert.False(allowList.TryGetByAuthority("unknown.host", out _));
    }

    [Fact]
    public async Task GatewayStore_Performs_Crud_On_All_Tables()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"lwt_test_gw_{Guid.NewGuid():N}.db");
        try
        {
            var dbFactory = DbSetup.CreateFactory($"Data Source={tempDb}");
            DbSetup.Initialize(dbFactory);

            var store = new GatewayStore(dbFactory);

            // 1. 路由
            var route = new GatewayRouteEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                RouteId = "r1",
                ClusterId = "c1",
                MatchPath = "/proxy/test/{**catch-all}",
                OrderNum = 10,
                IsEnabled = true
            };
            await store.InsertRouteAsync(route);
            var routes = (await store.GetAllRoutesAsync()).ToList();
            Assert.Single(routes);
            Assert.Equal("r1", routes[0].RouteId);

            // 2. 集群
            var cluster = new GatewayClusterEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                ClusterId = "c1",
                LoadBalancingPolicy = "RoundRobin",
                Destinations = "[{\"Address\":\"http://127.0.0.1:8080\"}]"
            };
            await store.InsertClusterAsync(cluster);
            var clusters = (await store.GetAllClustersAsync()).ToList();
            Assert.Single(clusters);
            Assert.Equal("c1", clusters[0].ClusterId);

            // 3. 网站
            var website = new GatewayWebsiteEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "pve",
                TargetUrl = "https://192.168.1.200:8006",
                IsEnabled = true
            };
            await store.InsertWebsiteAsync(website);
            var websites = (await store.GetAllWebsitesAsync()).ToList();
            Assert.Single(websites);
            Assert.Equal("pve", websites[0].Name);

            // 4. TCP 转发
            var tcp = new GatewayTcpRouteEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "ssh-forward",
                Protocol = "TCP",
                ListenPort = 2222,
                ForwardHost = "192.168.1.50",
                ForwardPort = 22,
                IsEnabled = true
            };
            await store.InsertTcpRouteAsync(tcp);
            var tcps = (await store.GetAllTcpRoutesAsync()).ToList();
            Assert.Single(tcps);
            Assert.Equal(2222, tcps[0].ListenPort);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/path", true)]
    [InlineData("http://localhost:3000", true)]
    [InlineData("http://[::1]:8080", true)]
    [InlineData("http://192.168.1.100:5211/video.mp4", true)]
    [InlineData("http://10.0.0.25:80", true)]
    [InlineData("http://172.20.0.5:8080", true)]
    [InlineData("http://100.80.1.2:8096", true)]
    [InlineData("http://alist:5211/d/local", true)]
    [InlineData("http://synology.local:5000", true)]
    [InlineData("http://openwrt.lan", true)]
    [InlineData("https://www.google.com/search", false)]
    [InlineData("https://alipan.com/download/file", false)]
    [InlineData("http://8.8.8.8:80", false)]
    public void NetworkAddressClassifier_Identifies_Private_And_Public_Urls(string url, bool expectedPrivate)
    {
        var actual = NetworkAddressClassifier.IsPrivateNetworkUrl(url);
        Assert.Equal(expectedPrivate, actual);
    }

    [Fact]
    public async Task FrpTunnelLineStore_Performs_Crud_With_Proxy_And_302()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"lwt_test_frp_{Guid.NewGuid():N}.db");
        try
        {
            var dbFactory = DbSetup.CreateFactory($"Data Source={tempDb}");
            DbSetup.Initialize(dbFactory);

            var store = new FrpTunnelLineStore(dbFactory);

            // 1. 新增线路 (含 SOCKS5 代理和 302 私网代拉)
            var line = new FrpTunnelLineEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "Emby 影音",
                ServerUrl = "wss://edge.example.com/frp",
                BackupServerUrls = "wss://backup.example.com/frp",
                TunnelHost = "emby",
                ApiKey = "secret-token",
                LocalTargetUrl = "http://192.168.1.150:8096",
                AutoStart = true,
                HeartbeatIntervalSeconds = 20,
                EnableLan302Proxy = true,
                ProxyType = "Socks5",
                ProxyUrl = "socks5://127.0.0.1:7890",
                ProxyBypass = "192.168.*",
                Status = "Disconnected",
                SortOrder = 1
            };

            await store.InsertLineAsync(line);

            // 2. 查询全部线路
            var allLines = await store.GetAllLinesAsync();
            Assert.Single(allLines);
            Assert.Equal("Emby 影音", allLines[0].Name);
            Assert.Equal("Socks5", allLines[0].ProxyType);
            Assert.True(allLines[0].EnableLan302Proxy);

            // 3. 按 Host 查询
            var byHost = await store.GetLineByHostAsync("emby");
            Assert.NotNull(byHost);
            Assert.Equal(line.Id, byHost.Id);

            // 4. 更新线路
            byHost.Name = "Emby 影音 (已更新)";
            byHost.ProxyType = "Http";
            byHost.ProxyUrl = "http://127.0.0.1:8080";
            await store.UpdateLineAsync(byHost);

            var updated = await store.GetLineByIdAsync(line.Id);
            Assert.NotNull(updated);
            Assert.Equal("Emby 影音 (已更新)", updated.Name);
            Assert.Equal("Http", updated.ProxyType);

            // 5. 更新状态
            await store.UpdateStatusAsync(line.Id, "Connected");
            var statusUpdated = await store.GetLineByIdAsync(line.Id);
            Assert.Equal("Connected", statusUpdated?.Status);

            // 6. 删除线路
            await store.DeleteLineAsync(line.Id);
            var remaining = await store.GetAllLinesAsync();
            Assert.Empty(remaining);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }

    [Theory]
    [InlineData("wss://p.asairo.de/frp", "lwt", "https://lwt.asairo.de/")]
    [InlineData("https://p.asairo.de", "lwt", "https://lwt.asairo.de/")]
    [InlineData("wss://p.asairo.de:8443/tunnel/connect", "lwt", "https://lwt.asairo.de:8443/")]
    [InlineData("wss://frp.example.com", "myapp", "https://myapp.example.com/")]
    [InlineData("wss://p.asairo.co.uk/frp", "lwt", "https://lwt.asairo.co.uk/")]
    [InlineData("wss://asairo.de/frp", "lwt", "https://lwt.asairo.de/")]
    [InlineData("http://p.asairo.de:8080", "lwt", "http://lwt.asairo.de:8080/")]
    [InlineData("p.asairo.de", "lwt", "https://lwt.asairo.de/")]
    public void FrpTunnelInstance_ResolveSubdomainUrl_Computes_Valid_Subdomain(string serverUrl, string tunnelHost, string expected)
    {
        var actual = FrpTunnelInstance.ResolveSubdomainUrl(serverUrl, tunnelHost);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("http://192.168.1.100:8080", "lwt")]
    [InlineData("https://127.0.0.1", "lwt")]
    [InlineData("wss://localhost:5000", "lwt")]
    [InlineData("https://worker.workers.dev", "lwt")]
    [InlineData("http://router.local", "lwt")]
    [InlineData("", "lwt")]
    [InlineData("wss://p.asairo.de", "")]
    public void FrpTunnelInstance_ResolveSubdomainUrl_Rejects_Invalid_Or_Ip_Addresses(string serverUrl, string tunnelHost)
    {
        var actual = FrpTunnelInstance.ResolveSubdomainUrl(serverUrl, tunnelHost);
        Assert.Null(actual);
    }
}
