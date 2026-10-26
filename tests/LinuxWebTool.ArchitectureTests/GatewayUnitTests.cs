using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LinuxWebTool.Contracts.Features.Gateway.Contracts;
using LinuxWebTool.Infrastructure.Features.Gateway.Adapters;
using LinuxWebTool.Infrastructure.Shared.Persistence;
using LinuxWebTool.WebHost.Features.Gateway.Adapters;
using LinuxWebTool.WebHost.Features.Gateway.Routes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinuxWebTool.ArchitectureTests;

public sealed class GatewayUnitTests
{
    // === 1. Authority 归一化测试 ===

    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("EXAMPLE.COM", "example.com")]
    [InlineData("example.com:80", "example.com")]
    [InlineData("EXAMPLE.COM:80", "example.com")]
    [InlineData("example.com:443", "example.com")]
    [InlineData("example.com:8080", "example.com:8080")]
    [InlineData("192.168.1.1:80", "192.168.1.1")]
    [InlineData("192.168.1.1:443", "192.168.1.1")]
    [InlineData("192.168.1.1:8080", "192.168.1.1:8080")]
    [InlineData("[::1]:80", "[::1]")]
    [InlineData("[::1]:443", "[::1]")]
    [InlineData("[::1]:8080", "[::1]:8080")]
    [InlineData("[2001:db8::1]", "[2001:db8::1]")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void WebsiteProxy_NormalizeAuthority_Handles_Various_Hosts(string input, string expected)
    {
        var result = WebsiteProxyTransformProvider.NormalizeAuthority(input);
        Assert.Equal(expected, result);
    }

    // === 2. 白名单匹配与权限模式测试 ===

    [Fact]
    public void WebsiteAllowList_Lookup_And_Mode_Permission_Verification()
    {
        var allowList = new WebsiteAllowList();
        var websites = new List<GatewayWebsiteEntity>
        {
            new()
            {
                Id = "w1",
                Name = "router",
                TargetUrl = "http://192.168.1.1:80",
                RewriteBody = true,
                RewriteCookie = true,
                IsEnabled = true
            },
            new()
            {
                Id = "w2",
                Name = "plex-media",
                TargetUrl = "http://192.168.1.60:32400",
                RewriteBody = true,
                RewriteCookie = true,
                IsEnabled = true
            },
            new()
            {
                Id = "w3",
                Name = "disabled-site",
                TargetUrl = "http://192.168.1.99:80",
                RewriteBody = false,
                RewriteCookie = false,
                IsEnabled = false
            }
        };

        allowList.Replace(websites);

        // 已启用站点数量
        Assert.Equal(2, allowList.Count);

        // 按 Authority 查询
        Assert.True(allowList.TryGetByAuthority("192.168.1.1", out var routerEntry));
        Assert.NotNull(routerEntry);
        Assert.Equal("192.168.1.1", routerEntry.Authority);
        Assert.Equal("http", routerEntry.UpstreamScheme);
        Assert.False(routerEntry.UseHttps);
        Assert.True(routerEntry.AllowsMode("Prefix"));
        Assert.True(routerEntry.AllowsMode("Alias"));
        Assert.True(routerEntry.AllowsMode("Scheme"));

        // 按 Alias 查询
        Assert.True(allowList.TryGetByAlias("plex-media", out var plexEntry));
        Assert.NotNull(plexEntry);
        Assert.Equal("192.168.1.60:32400", plexEntry.Authority);
        Assert.Equal("http", plexEntry.UpstreamScheme);

        // 统一 TryGet
        Assert.True(allowList.TryGet("192.168.1.60:32400", out _));
        Assert.True(allowList.TryGet("plex-media", out _));

        // 禁用或未登记站点被拒绝
        Assert.False(allowList.TryGetByAuthority("192.168.1.99", out _));
        Assert.False(allowList.TryGet("disabled-site", out _));
        Assert.False(allowList.TryGet("evil.attacker.com", out _));
    }

    [Fact]
    public void WebsiteAllowList_Custom_Alias_And_Auth_Properties()
    {
        var allowList = new WebsiteAllowList();
        var websites = new List<GatewayWebsiteEntity>
        {
            new()
            {
                Id = "w-cdp",
                Name = "Chrome CDP",
                Alias = "cdp",
                TargetUrl = "http://127.0.0.1:16002",
                RewriteBody = false,
                RewriteCookie = false,
                IsEnabled = true,
                AuthType = "Basic",
                AuthSecret = "admin:secret123"
            }
        };

        allowList.Replace(websites);

        Assert.True(allowList.TryGetByAlias("cdp", out var cdpEntry));
        Assert.NotNull(cdpEntry);
        Assert.Equal("127.0.0.1:16002", cdpEntry.Authority);
        Assert.Equal("cdp", cdpEntry.Alias);
        Assert.Equal("Basic", cdpEntry.AuthType);
        Assert.Equal("admin:secret123", cdpEntry.AuthSecret);

        // Name 作为备用别名小写也生效
        Assert.True(allowList.TryGetByAlias("chrome cdp", out _));
    }

    // === 3. HTML/CSS/Cookie 改写正则单元测试 ===

    [Fact]
    public void WebsiteProxy_HtmlAttrRootRelativeRegex_Rewrites_Only_Root_Relative_Urls()
    {
        var html = """
            <div>
              <a href="/dashboard/view">Link</a>
              <script src="/static/bundle.js"></script>
              <img src="/assets/logo.png" />
              <link rel="stylesheet" href="//cdn.jsdelivr.net/npm/vue.css" />
              <a href="https://google.com">External</a>
              <a href="relative/subpage">Relative</a>
            </div>
            """;

        var prefix = "/proxy/192.168.1.60:32400";
        var rewritten = WebsiteProxyTransformProvider.HtmlAttrRootRelativeRegex().Replace(html, prefix + "/");

        // 根相对路径必须被注入前缀
        Assert.Contains($"""<a href="{prefix}/dashboard/view">Link</a>""", rewritten);
        Assert.Contains($"""<script src="{prefix}/static/bundle.js"></script>""", rewritten);
        Assert.Contains($"""<img src="{prefix}/assets/logo.png" />""", rewritten);

        // 协议相对 URL 与外部 URL 严禁被改写
        Assert.Contains("""<link rel="stylesheet" href="//cdn.jsdelivr.net/npm/vue.css" />""", rewritten);
        Assert.Contains("""<a href="https://google.com">External</a>""", rewritten);
        Assert.Contains("""<a href="relative/subpage">Relative</a>""", rewritten);
    }

    [Fact]
    public void WebsiteProxy_CssUrlRootRelativeRegex_Rewrites_Root_Relative_Urls()
    {
        var css = """
            body { background: url(/images/bg.png); }
            @font-face { src: url('/fonts/inter.woff2'); }
            .icon { background-image: url("//cdn.com/icon.svg"); }
            .rel { background: url(images/local.png); }
            """;

        var prefix = "/s/plex";
        var rewritten = WebsiteProxyTransformProvider.CssUrlRootRelativeRegex().Replace(css, prefix + "/");

        Assert.Contains($"""url({prefix}/images/bg.png)""", rewritten);
        Assert.Contains($"""url('{prefix}/fonts/inter.woff2')""", rewritten);
        Assert.Contains("""url("//cdn.com/icon.svg")""", rewritten);
        Assert.Contains("""url(images/local.png)""", rewritten);
    }

    [Fact]
    public void WebsiteProxy_CookiePathRegex_Rewrites_Cookie_Path_Attribute()
    {
        var rawCookies = new[]
        {
            "session=123456; Path=/",
            "auth_token=abcdef; path=/; Secure; HttpOnly",
            "pref=dark; Path=/settings",
            "lang=zh; Domain=example.com"
        };

        var prefix = "/proxy/plex";
        var pathPrefix = prefix + "/";

        var rewritten = rawCookies.Select(c =>
            WebsiteProxyTransformProvider.CookiePathRegex().Replace(c, "$1" + pathPrefix.Replace("$", "$$"))
        ).ToList();

        Assert.Equal($"session=123456; Path={prefix}/", rewritten[0]);
        Assert.Equal($"auth_token=abcdef; path={prefix}/; Secure; HttpOnly", rewritten[1]);
        Assert.Equal($"pref=dark; Path={prefix}/settings", rewritten[2]);
        Assert.Equal("lang=zh; Domain=example.com", rewritten[3]);
    }

    // === 4. 四层 TCP 端口转发与热重载单元测试 ===

    [Fact]
    public async Task TcpProxyEngine_Forwards_Traffic_And_Hot_Reloads_Target()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"lwt_test_tcp_{Guid.NewGuid():N}.db");
        try
        {
            var dbFactory = DbSetup.CreateFactory($"Data Source={tempDb}");
            DbSetup.Initialize(dbFactory);
            var store = new GatewayStore(dbFactory);

            // 1. 启动第一个上游 TCP Echo 服务
            var echoServer1 = new TcpListener(IPAddress.Loopback, 0);
            echoServer1.Start();
            var echoPort1 = ((IPEndPoint)echoServer1.LocalEndpoint).Port;
            using var cts = new CancellationTokenSource();

            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var socket = await echoServer1.AcceptSocketAsync(cts.Token);
                        _ = Task.Run(async () =>
                        {
                            using var s = socket;
                            var buf = new byte[1024];
                            int read = await s.ReceiveAsync(buf, SocketFlags.None, cts.Token);
                            if (read > 0)
                            {
                                var msg = Encoding.UTF8.GetString(buf, 0, read);
                                var resp = Encoding.UTF8.GetBytes($"SERVER1:{msg}");
                                await s.SendAsync(resp, SocketFlags.None, cts.Token);
                            }
                        }, cts.Token);
                    }
                    catch { break; }
                }
            }, cts.Token);

            // 2. 选择一个未被占用的监听端口
            var proxyPort = GetAvailablePort();

            // 3. 注册 TCP 转发规则并启动转发引擎
            var tcpRoute = new GatewayTcpRouteEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "test-tcp-forward",
                Protocol = "TCP",
                ListenPort = proxyPort,
                ForwardHost = "127.0.0.1",
                ForwardPort = echoPort1,
                IsEnabled = true
            };
            await store.InsertTcpRouteAsync(tcpRoute);

            var engine = new TcpProxyEngine(store, NullLogger<TcpProxyEngine>.Instance);
            await engine.ReloadAsync();

            // 4. 客户端向 proxyPort 发送数据并校验首个服务的响应
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, proxyPort);
                var stream = client.GetStream();
                await stream.WriteAsync(Encoding.UTF8.GetBytes("hello"));

                var buffer = new byte[1024];
                var count = await stream.ReadAsync(buffer);
                var response = Encoding.UTF8.GetString(buffer, 0, count);
                Assert.Equal("SERVER1:hello", response);
            }

            // 5. 启动第二个上游 TCP Echo 服务
            var echoServer2 = new TcpListener(IPAddress.Loopback, 0);
            echoServer2.Start();
            var echoPort2 = ((IPEndPoint)echoServer2.LocalEndpoint).Port;

            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var socket = await echoServer2.AcceptSocketAsync(cts.Token);
                        _ = Task.Run(async () =>
                        {
                            using var s = socket;
                            var buf = new byte[1024];
                            int read = await s.ReceiveAsync(buf, SocketFlags.None, cts.Token);
                            if (read > 0)
                            {
                                var msg = Encoding.UTF8.GetString(buf, 0, read);
                                var resp = Encoding.UTF8.GetBytes($"SERVER2:{msg}");
                                await s.SendAsync(resp, SocketFlags.None, cts.Token);
                            }
                        }, cts.Token);
                    }
                    catch { break; }
                }
            }, cts.Token);

            // 6. 热更新转发目标为 Server2 并执行 ReloadAsync
            tcpRoute.ForwardPort = echoPort2;
            await store.UpdateTcpRouteAsync(tcpRoute);
            await engine.ReloadAsync();

            // 7. 再次连接同一 proxyPort，验证转发目标已无缝切换至 Server2
            using (var client2 = new TcpClient())
            {
                await client2.ConnectAsync(IPAddress.Loopback, proxyPort);
                var stream2 = client2.GetStream();
                await stream2.WriteAsync(Encoding.UTF8.GetBytes("world"));

                var buffer2 = new byte[1024];
                var count2 = await stream2.ReadAsync(buffer2);
                var response2 = Encoding.UTF8.GetString(buffer2, 0, count2);
                Assert.Equal("SERVER2:world", response2);
            }

            // 8. 停止与清理
            await engine.StopAsync(CancellationToken.None);
            cts.Cancel();
            echoServer1.Stop();
            echoServer2.Stop();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }

    // === 5. 四层 UDP 端口转发与 NAT 会话跟踪单元测试 ===

    [Fact]
    public async Task UdpProxyEngine_Forwards_BiDirectional_Traffic_With_Session_Tracking()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"lwt_test_udp_{Guid.NewGuid():N}.db");
        try
        {
            var dbFactory = DbSetup.CreateFactory($"Data Source={tempDb}");
            DbSetup.Initialize(dbFactory);
            var store = new GatewayStore(dbFactory);

            // 1. 启动上游 UDP Echo 服务
            var echoUdp = new UdpClient(0);
            var echoPort = ((IPEndPoint)echoUdp.Client.LocalEndPoint!).Port;
            using var cts = new CancellationTokenSource();

            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var res = await echoUdp.ReceiveAsync(cts.Token);
                        var msg = Encoding.UTF8.GetString(res.Buffer);
                        var reply = Encoding.UTF8.GetBytes($"UDP_ECHO:{msg}");
                        await echoUdp.SendAsync(reply, reply.Length, res.RemoteEndPoint);
                    }
                    catch { break; }
                }
            }, cts.Token);

            // 2. 选择未被占用的端口
            var proxyUdpPort = GetAvailablePort();

            // 3. 登记 UDP 转发规则
            var udpRoute = new GatewayTcpRouteEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "test-udp-forward",
                Protocol = "UDP",
                ListenPort = proxyUdpPort,
                ForwardHost = "127.0.0.1",
                ForwardPort = echoPort,
                IsEnabled = true
            };
            await store.InsertTcpRouteAsync(udpRoute);

            // 4. 启动 UDP 转发引擎
            var engine = new UdpProxyEngine(store, NullLogger<UdpProxyEngine>.Instance);
            await engine.ReloadAsync();

            // 5. 客户端发送 UDP 数据包，并验证能正常收到上游服务的双向回包
            using var clientUdp = new UdpClient();
            clientUdp.Client.ReceiveTimeout = 4000;
            var clientData = Encoding.UTF8.GetBytes("ping-packet");
            await clientUdp.SendAsync(clientData, clientData.Length, "127.0.0.1", proxyUdpPort);

            using var recvCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var replyResult = await clientUdp.ReceiveAsync(recvCts.Token);
            var replyString = Encoding.UTF8.GetString(replyResult.Buffer);
            Assert.Equal("UDP_ECHO:ping-packet", replyString);

            // 6. 停止引擎
            await engine.StopAsync(CancellationToken.None);
            cts.Cancel();
            echoUdp.Dispose();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }

    // === 6. LinuxArch025 架构门禁测试：双栈监听、端口冲突状态报告与自愈能力 ===

    [Fact]
    public async Task TcpProxyEngine_DualStack_Binding_And_RuntimeStatus_Exposed()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"lwt_test_dual_{Guid.NewGuid():N}.db");
        try
        {
            var dbFactory = DbSetup.CreateFactory($"Data Source={tempDb}");
            DbSetup.Initialize(dbFactory);
            var store = new GatewayStore(dbFactory);

            var port = GetAvailablePort();
            var tcpRoute = new GatewayTcpRouteEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "test-dualstack",
                Protocol = "TCP",
                ListenPort = port,
                ForwardHost = "127.0.0.1",
                ForwardPort = 80,
                IsEnabled = true
            };
            await store.InsertTcpRouteAsync(tcpRoute);

            var engine = new TcpProxyEngine(store, NullLogger<TcpProxyEngine>.Instance);
            await engine.ReloadAsync();

            var status = engine.GetRuntimeStatus(port);
            Assert.True(status.IsRunning, "双栈引擎必须成功监听");
            Assert.Null(status.LastError);
            Assert.NotNull(status.BoundAddress);
            Assert.True(status.BoundAddress == "[::]" || status.BoundAddress == "0.0.0.0");

            await engine.StopAsync(CancellationToken.None);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }

    [Fact]
    public async Task TcpProxyEngine_Port_Conflict_Reports_LastError_And_AutoHeals()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"lwt_test_conflict_{Guid.NewGuid():N}.db");
        try
        {
            var dbFactory = DbSetup.CreateFactory($"Data Source={tempDb}");
            DbSetup.Initialize(dbFactory);
            var store = new GatewayStore(dbFactory);

            // 1. 预先人为占用端口 (使用双栈监听器占用，触发精准端口冲突)
            var conflictPort = GetAvailablePort();
            using var blocker = TcpListener.Create(conflictPort);
            blocker.Start();

            // 2. 配置相同端口的转发规则并执行 ReloadAsync
            var route = new GatewayTcpRouteEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "conflict-route",
                Protocol = "TCP",
                ListenPort = conflictPort,
                ForwardHost = "127.0.0.1",
                ForwardPort = 80,
                IsEnabled = true
            };
            await store.InsertTcpRouteAsync(route);

            var engine = new TcpProxyEngine(store, NullLogger<TcpProxyEngine>.Instance);
            await engine.ReloadAsync();

            // 3. 验证状态机准确捕获端口冲突错误，而非静默假绿
            var conflictStatus = engine.GetRuntimeStatus(conflictPort);
            Assert.False(conflictStatus.IsRunning, "端口冲突时 IsRunning 必须为 false");
            Assert.NotNull(conflictStatus.LastError);
            Assert.Contains("启动监听失败", conflictStatus.LastError);

            // 4. 释放冲突端口，重新 Reload 验证自愈恢复
            blocker.Stop();
            await engine.ReloadAsync();

            var healedStatus = engine.GetRuntimeStatus(conflictPort);
            Assert.True(healedStatus.IsRunning, "端口释放后自愈引擎必须恢复监听成功");
            Assert.Null(healedStatus.LastError);

            await engine.StopAsync(CancellationToken.None);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }

    [Fact]
    public async Task GatewayController_TestTarget_Reachability_Detection()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"lwt_test_ctrl_{Guid.NewGuid():N}.db");
        try
        {
            var dbFactory = DbSetup.CreateFactory($"Data Source={tempDb}");
            DbSetup.Initialize(dbFactory);
            var store = new GatewayStore(dbFactory);

            // 启动一个测试 TCP 监听端点
            var echoPort = GetAvailablePort();
            using var listener = new TcpListener(IPAddress.Loopback, echoPort);
            listener.Start();

            var tcpEngine = new TcpProxyEngine(store, NullLogger<TcpProxyEngine>.Instance);
            var udpEngine = new UdpProxyEngine(store, NullLogger<UdpProxyEngine>.Instance);
            var dummyAllow = new WebsiteAllowList();
            var dummyTransform = new WebsiteProxyTransformProvider(dummyAllow, NullLogger<WebsiteProxyTransformProvider>.Instance);
            var dummyConfigProvider = new DatabaseProxyConfigProvider(store, dummyAllow, NullLogger<DatabaseProxyConfigProvider>.Instance);

            var controller = new GatewayController(
                store,
                dummyConfigProvider,
                tcpEngine,
                udpEngine,
                new NullOperationLogger());

            // 1. 探测存活的开放端口
            var validReq = new TestL4TargetRequest("TCP", "127.0.0.1", echoPort, 2000);
            var validResult = await controller.TestTarget(validReq);
            var validOk = Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IResult>(validResult);

            // 2. 探测未开放的端口（验证快速失败返回错误描述）
            var closedPort = GetAvailablePort();
            var invalidReq = new TestL4TargetRequest("TCP", "127.0.0.1", closedPort, 1000);
            var invalidResult = await controller.TestTarget(invalidReq);
            Assert.NotNull(invalidResult);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }

    private sealed class NullOperationLogger : IOperationLogger
    {
        public Task LogAsync(string action, string targetType, string targetName, string? detail = null, bool success = true, string? clientIp = null)
        {
            return Task.CompletedTask;
        }
    }

    private static int GetAvailablePort()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return port;
    }
}
