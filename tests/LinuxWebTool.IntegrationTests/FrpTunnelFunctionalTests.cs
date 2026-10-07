using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LinuxWebTool.Contracts.Models;
using LinuxWebTool.Infrastructure.Persistence.Entities;
using LinuxWebTool.Infrastructure.Tunnel;
using LinuxWebTool.IntegrationTests.Support;
using LinuxWebTool.WebHost.Composition;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinuxWebTool.IntegrationTests;

public sealed class FrpTunnelFunctionalTests
{
    [Fact]
    public async Task FrpTunnel_Config_And_Lines_Crud_Api()
    {
        var client = await TestServerFixture.CreateAdminClientAsync();

        // 1. 查询默认配置
        var configResp = await client.GetAsync("/api/FrpTunnel/Config");
        Assert.Equal(HttpStatusCode.OK, configResp.StatusCode);
        var configDto = JsonSerializer.Deserialize<FrpTunnelConfigDto>(await configResp.Content.ReadAsStringAsync(), AppJsonSerializerContext.Default.Options)!;
        Assert.NotNull(configDto);

        // 2. 更新配置
        var updateConfigReq = new UpdateFrpConfigRequest(
            ServerUrl: "wss://edge-alpha.test.com",
            TunnelHost: "my-alpha-host",
            ApiKey: "new-secret-api-key",
            LocalTargetUrl: "http://127.0.0.1:8080",
            AutoStart: false,
            HeartbeatIntervalSeconds: 20);

        var updateConfigJson = JsonSerializer.Serialize(updateConfigReq, AppJsonSerializerContext.Default.UpdateFrpConfigRequest);
        var updateConfigResp = await client.PutAsync("/api/FrpTunnel/Config", TestServerFixture.Json(updateConfigJson));
        Assert.Equal(HttpStatusCode.OK, updateConfigResp.StatusCode);

        // 3. 创建多线路条目 (包含 SOCKS5 代理和 302 私网代拉流)
        var createLineReq = new CreateFrpTunnelLineRequest(
            Name: "Emby Media Line",
            ServerUrl: "wss://edge.example.com/frp",
            BackupServerUrls: "wss://backup.example.com/frp",
            TunnelHost: "emby-home",
            ApiKey: "secret-emby-token-123456",
            LocalTargetUrl: "http://192.168.1.150:8096",
            AutoStart: false,
            HeartbeatIntervalSeconds: 15,
            EnableLan302Proxy: true,
            ProxyType: "Socks5",
            ProxyUrl: "socks5://127.0.0.1:7890",
            ProxyBypass: "192.168.*",
            SortOrder: 1);

        var createLineJson = JsonSerializer.Serialize(createLineReq, AppJsonSerializerContext.Default.CreateFrpTunnelLineRequest);
        var createLineResp = await client.PostAsync("/api/FrpTunnel/Lines", TestServerFixture.Json(createLineJson));
        Assert.Equal(HttpStatusCode.OK, createLineResp.StatusCode);

        using var createdDoc = JsonDocument.Parse(await createLineResp.Content.ReadAsStringAsync());
        var lineId = createdDoc.RootElement.GetProperty("id").GetString()!;
        var maskedKey = createdDoc.RootElement.GetProperty("apiKey").GetString()!;

        // 校验敏感 Key 脱敏保护（严禁返回原始密码）
        Assert.Contains("*", maskedKey);
        Assert.DoesNotContain("secret-emby-token-123456", maskedKey);

        // 4. 查询多线路列表
        var linesResp = await client.GetAsync("/api/FrpTunnel/Lines");
        Assert.Equal(HttpStatusCode.OK, linesResp.StatusCode);
        var lines = JsonSerializer.Deserialize<List<FrpTunnelLineDto>>(await linesResp.Content.ReadAsStringAsync(), AppJsonSerializerContext.Default.Options)!;
        var foundLine = lines.FirstOrDefault(l => l.Id == lineId);
        Assert.NotNull(foundLine);
        Assert.Equal("Emby Media Line", foundLine.Name);
        Assert.Equal("Socks5", foundLine.ProxyType);
        Assert.True(foundLine.EnableLan302Proxy);
        Assert.Equal("https://emby-home.example.com/", foundLine.SubdomainUrl);

        // 5. 更新线路属性
        var updateLineReq = new UpdateFrpTunnelLineRequest(
            Name: "Emby Media Line (Updated)",
            ServerUrl: "wss://edge.example.com/frp",
            BackupServerUrls: "wss://backup.example.com/frp",
            TunnelHost: "emby-home",
            ApiKey: "secret-emby-token-123456",
            LocalTargetUrl: "http://192.168.1.150:8096",
            AutoStart: false,
            HeartbeatIntervalSeconds: 25,
            EnableLan302Proxy: true,
            ProxyType: "Http",
            ProxyUrl: "http://127.0.0.1:8888",
            ProxyBypass: "192.168.*",
            SortOrder: 2);

        var updateLineJson = JsonSerializer.Serialize(updateLineReq, AppJsonSerializerContext.Default.UpdateFrpTunnelLineRequest);
        var updateLineResp = await client.PutAsync($"/api/FrpTunnel/Lines/{lineId}", TestServerFixture.Json(updateLineJson));
        Assert.Equal(HttpStatusCode.OK, updateLineResp.StatusCode);

        // 6. 获取线路日志
        var logsResp = await client.GetAsync($"/api/FrpTunnel/Lines/{lineId}/Logs");
        Assert.Equal(HttpStatusCode.OK, logsResp.StatusCode);

        // 7. 停止线路
        var stopResp = await client.PostAsync($"/api/FrpTunnel/Lines/{lineId}/Stop", null);
        Assert.Equal(HttpStatusCode.OK, stopResp.StatusCode);

        // 8. 删除线路
        var deleteResp = await client.DeleteAsync($"/api/FrpTunnel/Lines/{lineId}");
        Assert.Equal(HttpStatusCode.OK, deleteResp.StatusCode);

        // 9. 确认线路已彻底删除
        var afterLines = JsonSerializer.Deserialize<List<FrpTunnelLineDto>>(await (await client.GetAsync("/api/FrpTunnel/Lines")).Content.ReadAsStringAsync(), AppJsonSerializerContext.Default.Options)!;
        Assert.DoesNotContain(afterLines, l => l.Id == lineId);
    }

    [Fact]
    public async Task FrpTunnel_302_Lan_Stream_Proxy_And_Cdn_PassThrough()
    {
        // 动态分配可用端口并启动本地模拟 HTTP 站点
        var port = GetFreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // 后台监听处理请求
        var serverTask = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync();
                }
                catch
                {
                    break;
                }

                var path = ctx.Request.Url?.AbsolutePath ?? "/";
                if (path == "/vod/redirect-to-lan")
                {
                    // 模拟本地影视服务返回 302 重定向至局域网目标
                    ctx.Response.StatusCode = (int)HttpStatusCode.Found;
                    ctx.Response.Headers.Add("Location", $"http://127.0.0.1:{port}/internal/media.mp4");
                    ctx.Response.Close();
                }
                else if (path == "/internal/media.mp4")
                {
                    // 局域网真实流媒体文件内容
                    var payload = Encoding.UTF8.GetBytes("LWT_LAN_STREAM_CONTENT_BYTES_2026");
                    ctx.Response.StatusCode = (int)HttpStatusCode.OK;
                    ctx.Response.ContentType = "video/mp4";
                    ctx.Response.ContentLength64 = payload.Length;
                    await ctx.Response.OutputStream.WriteAsync(payload, cts.Token);
                    ctx.Response.Close();
                }
                else if (path == "/vod/redirect-to-cdn")
                {
                    // 模拟返回公网 CDN 直链（如阿里云盘/115/R2/COS）
                    ctx.Response.StatusCode = (int)HttpStatusCode.Found;
                    ctx.Response.Headers.Add("Location", "https://cdn.example-cloud.com/video.mp4");
                    ctx.Response.Close();
                }
                else
                {
                    ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    ctx.Response.Close();
                }
            }
        }, cts.Token);

        try
        {
            // === 场景 A: 启用 EnableLan302Proxy = true，遇到局域网 302 自动代拉流 ===
            var configWithProxy = new FrpTunnelLineEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "LAN 302 Test Line",
                ServerUrl = "wss://tunnel.test.com",
                TunnelHost = "test",
                ApiKey = "token",
                LocalTargetUrl = $"http://127.0.0.1:{port}",
                EnableLan302Proxy = true
            };

            var instanceWithProxy = new FrpTunnelInstance(configWithProxy, NullLogger<FrpTunnelInstance>.Instance);

            using var reqLan = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/vod/redirect-to-lan");
            var (respLan, followedLan, finalUrl) = await instanceWithProxy.ExecuteLocalHttpWith302Async(
                reqLan,
                $"http://127.0.0.1:{port}/vod/redirect-to-lan",
                "GET",
                null,
                cts.Token);

            using (respLan)
            {
                // 验证已自动跟进私网重定向，并直接拿到 200 OK 流数据
                Assert.True(followedLan, "应当成功识别局域网私有地址并代拉流");
                Assert.Equal(HttpStatusCode.OK, respLan.StatusCode);
                Assert.EndsWith("/internal/media.mp4", finalUrl);
                var content = await respLan.Content.ReadAsStringAsync(cts.Token);
                Assert.Equal("LWT_LAN_STREAM_CONTENT_BYTES_2026", content);
            }

            // === 场景 B: 启用 EnableLan302Proxy = true，遇到公网 CDN 302 智能放行直链 ===
            using var reqCdn = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/vod/redirect-to-cdn");
            var (respCdn, followedCdn, finalCdnUrl) = await instanceWithProxy.ExecuteLocalHttpWith302Async(
                reqCdn,
                $"http://127.0.0.1:{port}/vod/redirect-to-cdn",
                "GET",
                null,
                cts.Token);

            using (respCdn)
            {
                // 验证公网 CDN 302 保持放行，不走局域网代拉
                Assert.False(followedCdn, "公网 CDN 不应由家庭局域网代拉，应保持 302 放行直链");
                Assert.Equal(HttpStatusCode.Found, respCdn.StatusCode);
                Assert.Equal("https://cdn.example-cloud.com/video.mp4", respCdn.Headers.Location?.ToString());
            }

            // === 场景 C: 禁用 EnableLan302Proxy = false，原样返回 302 ===
            var configNoProxy = new FrpTunnelLineEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "No 302 Line",
                ServerUrl = "wss://tunnel.test.com",
                TunnelHost = "test",
                ApiKey = "token",
                LocalTargetUrl = $"http://127.0.0.1:{port}",
                EnableLan302Proxy = false
            };

            var instanceNoProxy = new FrpTunnelInstance(configNoProxy, NullLogger<FrpTunnelInstance>.Instance);

            using var reqDisabled = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/vod/redirect-to-lan");
            var (respDisabled, followedDisabled, _) = await instanceNoProxy.ExecuteLocalHttpWith302Async(
                reqDisabled,
                $"http://127.0.0.1:{port}/vod/redirect-to-lan",
                "GET",
                null,
                cts.Token);

            using (respDisabled)
            {
                Assert.False(followedDisabled);
                Assert.Equal(HttpStatusCode.Found, respDisabled.StatusCode);
            }
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            try { await serverTask; } catch { }
        }
    }

    private static int GetFreePort()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return port;
    }

    [Fact]
    public void TunnelFrameSerializer_Produces_Valid_Json_Without_Reflection()
    {
        // 1. 测试标准 HTTP_RESPONSE 帧序列化
        var headers = new Dictionary<string, string>
        {
            ["content-type"] = "application/json",
            ["server"] = "PlexMediaServer"
        };
        var bodyB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"ok\":true}"));
        var respBytes = TunnelFrameSerializer.SerializeHttpResponse("req-12345", 200, headers, bodyB64);
        Assert.NotNull(respBytes);
        Assert.True(respBytes.Length > 0);

        using (var doc = JsonDocument.Parse(respBytes))
        {
            var root = doc.RootElement;
            Assert.Equal("HTTP_RESPONSE", root.GetProperty("type").GetString());
            Assert.Equal("req-12345", root.GetProperty("requestId").GetString());
            Assert.Equal(200, root.GetProperty("status").GetInt32());
            Assert.Equal(bodyB64, root.GetProperty("body").GetString());
            Assert.Equal("application/json", root.GetProperty("headers").GetProperty("content-type").GetString());
            Assert.Equal("PlexMediaServer", root.GetProperty("headers").GetProperty("server").GetString());
        }

        // 2. 测试流式起点帧 HTTP_RESPONSE_START
        var startBytes = TunnelFrameSerializer.SerializeHttpResponseStart("req-stream", 206, headers);
        using (var doc = JsonDocument.Parse(startBytes))
        {
            var root = doc.RootElement;
            Assert.Equal("HTTP_RESPONSE_START", root.GetProperty("type").GetString());
            Assert.Equal("req-stream", root.GetProperty("requestId").GetString());
            Assert.Equal(206, root.GetProperty("status").GetInt32());
            Assert.Equal("PlexMediaServer", root.GetProperty("headers").GetProperty("server").GetString());
        }

        // 3. 测试流式结束帧 HTTP_RESPONSE_END
        var endBytes = TunnelFrameSerializer.SerializeHttpResponseEnd("req-stream");
        using (var doc = JsonDocument.Parse(endBytes))
        {
            var root = doc.RootElement;
            Assert.Equal("HTTP_RESPONSE_END", root.GetProperty("type").GetString());
            Assert.Equal("req-stream", root.GetProperty("requestId").GetString());
        }
    }

    [Fact]
    public void TunnelFrameSerializer_Sanitizes_ByteString_Headers()
    {
        // 测试包含非 ASCII 中文字符的标头值（WHATWG Fetch ByteString 安全）
        var rawLocation = "http://192.168.1.60:5211/play/测试视频.mkv";
        var sanitized = TunnelFrameSerializer.SanitizeHeaderValue(rawLocation);

        Assert.NotNull(sanitized);
        Assert.DoesNotContain("测试视频", sanitized);
        Assert.All(sanitized, c => Assert.True(c <= 127, $"Character '{c}' has codepoint > 127"));

        var headers = new Dictionary<string, string>
        {
            ["Location"] = rawLocation,
            ["Content-Disposition"] = "attachment; filename=\"测试.mp4\""
        };
        var bytes = TunnelFrameSerializer.SerializeHttpResponse("req-chinese", 302, headers, "");
        using var doc = JsonDocument.Parse(bytes);
        var headersElem = doc.RootElement.GetProperty("headers");

        var locVal = headersElem.GetProperty("Location").GetString()!;
        Assert.All(locVal, c => Assert.True(c <= 127));

        var dispVal = headersElem.GetProperty("Content-Disposition").GetString()!;
        Assert.All(dispVal, c => Assert.True(c <= 127));
    }

    [Fact]
    public void CombineLocalTargetUri_Preserves_Subpaths_And_Queries()
    {
        // 1. 基础无子路径拼接
        var uri1 = FrpTunnelInstance.CombineLocalTargetUri("http://192.168.1.60:32400", "/");
        Assert.Equal("http://192.168.1.60:32400/", uri1.ToString());

        var uri2 = FrpTunnelInstance.CombineLocalTargetUri("http://192.168.1.60:32400", "/web/index.html");
        Assert.Equal("http://192.168.1.60:32400/web/index.html", uri2.ToString());

        // 2. 带基准子路径拼接
        var uri3 = FrpTunnelInstance.CombineLocalTargetUri("http://127.0.0.1:3000/api", "/users");
        Assert.Equal("http://127.0.0.1:3000/api/users", uri3.ToString());

        var uri4 = FrpTunnelInstance.CombineLocalTargetUri("http://127.0.0.1:3000/api/", "/users?id=123&page=1");
        Assert.Equal("http://127.0.0.1:3000/api/users?id=123&page=1", uri4.ToString());

        // 3. 相对路径无斜杠
        var uri5 = FrpTunnelInstance.CombineLocalTargetUri("http://127.0.0.1:3000/api", "v1/status");
        Assert.Equal("http://127.0.0.1:3000/api/v1/status", uri5.ToString());
    }

    [Fact]
    public void MediaStreamCoordinator_RangeProbe_And_Preemption()
    {
        // 1. RFC 9110 Range 探测识别
        Assert.True(MediaStreamCoordinator.IsBoundedRangeProbe("bytes=-524288")); // 后缀切片探针
        Assert.True(MediaStreamCoordinator.IsBoundedRangeProbe("bytes=0-1048576")); // 1MB 短切片探针
        Assert.False(MediaStreamCoordinator.IsBoundedRangeProbe("bytes=0-")); // 开放全量拉流
        Assert.False(MediaStreamCoordinator.IsBoundedRangeProbe("bytes=104857600-")); // 播放寻轨开放拉流
        Assert.False(MediaStreamCoordinator.IsBoundedRangeProbe(null));

        // 2. 媒体资源判定
        Assert.True(MediaStreamCoordinator.IsMediaResource("/library/parts/51759/1790860244/file"));
        Assert.True(MediaStreamCoordinator.IsMediaResource("/videos/123/stream.mkv"));
        Assert.False(MediaStreamCoordinator.IsMediaResource("/web/index.html"));
        Assert.False(MediaStreamCoordinator.IsMediaResource("/api/users"));

        // 3. 会话作用域提取
        var headersPlex = new Dictionary<string, string> { ["x-plex-client-identifier"] = "plex-web-client-01" };
        Assert.Equal("client:plex-web-client-01", MediaStreamCoordinator.ExtractSessionScope(headersPlex, "/"));

        var headersIp = new Dictionary<string, string> { ["cf-connecting-ip"] = "1.2.3.4" };
        Assert.Equal("ip:1.2.3.4", MediaStreamCoordinator.ExtractSessionScope(headersIp, "/"));

        // 4. 媒体流抢占逻辑
        var coordinator = new MediaStreamCoordinator();
        var cts = new CancellationTokenSource();

        var session1 = new TunnelRequestSession("req-1", "GET", "/library/parts/1/file", cts.Token);
        var canonicalKey = MediaStreamCoordinator.GetCanonicalMediaKey("client:test", "/library/parts/1/file");

        // 短探针注册：不抢占
        var (sOldProbe, wasProbePreempted) = coordinator.CoordinateStream(canonicalKey, session1, isProbe: true);
        Assert.Null(sOldProbe);
        Assert.False(wasProbePreempted);
        Assert.False(session1.Cts.IsCancellationRequested);

        // 主拉流注册
        var (sOld1, wasPreempted1) = coordinator.CoordinateStream(canonicalKey, session1, isProbe: false);
        Assert.Null(sOld1);
        Assert.False(wasPreempted1);

        // 模拟用户拖进度条发起 session2
        var session2 = new TunnelRequestSession("req-2", "GET", "/library/parts/1/file", cts.Token);
        var (sOld2, wasPreempted2) = coordinator.CoordinateStream(canonicalKey, session2, isProbe: false);

        Assert.True(wasPreempted2);
        Assert.Same(session1, sOld2);
        Assert.True(session1.Cts.IsCancellationRequested); // 旧流 session1 被即时中止！
        Assert.Equal(TunnelRequestState.Aborted, session1.State);
        Assert.False(session2.Cts.IsCancellationRequested); // 新流 session2 正常运行
        // 5. 验证快速寻轨取消并解注册后，后续请求不会抛出 NullReferenceException 导致长连接中断
        coordinator.Unregister(canonicalKey, session1);
        var session3 = new TunnelRequestSession("req-3", "GET", "/library/parts/1/file", cts.Token);
        var exception = Record.Exception(() => coordinator.CoordinateStream(canonicalKey, session3, isProbe: false));
        Assert.Null(exception);

        // 6. 验证从 URL 查询参数中提取 X-Plex-Token 与 X-Plex-Client-Identifier
        var emptyHeaders = new Dictionary<string, string>();
        var scopeFromToken = MediaStreamCoordinator.ExtractSessionScope(emptyHeaders, "/library/parts/70470/file?X-Plex-Token=token_abc123");
        Assert.Equal("token:token_abc123", scopeFromToken);

        var scopeFromClient = MediaStreamCoordinator.ExtractSessionScope(emptyHeaders, "/library/parts/70470/file?X-Plex-Client-Identifier=client_xyz");
        Assert.Equal("client:client_xyz", scopeFromClient);

        // 7. 验证 302 直链微缓存及失效自愈
        coordinator.CacheRedirect(canonicalKey, "http://192.168.1.60:5211/strm/video.mp4");
        Assert.Equal("http://192.168.1.60:5211/strm/video.mp4", coordinator.GetCachedRedirect(canonicalKey));

        coordinator.InvalidateRedirect(canonicalKey);
        Assert.Null(coordinator.GetCachedRedirect(canonicalKey));
    }

    [Fact]
    public async Task ExecuteLocalHttpWith302_Preserves_Range_And_UserAgent_Headers_On_LanRedirect()
    {
        var port = GetFreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        string? receivedRange = null;
        string? receivedUa = null;

        var serverTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync();
                }
                catch when (cts.IsCancellationRequested) { break; }
                catch { break; }

                if (ctx.Request.RawUrl == "/vod/strm-source")
                {
                    ctx.Response.StatusCode = 302;
                    ctx.Response.Headers["Location"] = $"http://127.0.0.1:{port}/internal/strm-stream";
                    ctx.Response.Close();
                }
                else if (ctx.Request.RawUrl == "/internal/strm-stream")
                {
                    receivedRange = ctx.Request.Headers["Range"];
                    receivedUa = ctx.Request.Headers["User-Agent"];

                    ctx.Response.StatusCode = 206;
                    ctx.Response.Headers["Content-Range"] = "bytes 1531576320-1531576330/2000000000";
                    var data = Encoding.UTF8.GetBytes("RANGE_DATA_TEST");
                    ctx.Response.ContentLength64 = data.Length;
                    await ctx.Response.OutputStream.WriteAsync(data, cts.Token);
                    ctx.Response.Close();
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                }
            }
        }, cts.Token);

        try
        {
            var config = new FrpTunnelLineEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "STRM Range Line",
                ServerUrl = "wss://tunnel.test.com",
                TunnelHost = "test",
                ApiKey = "token",
                LocalTargetUrl = $"http://127.0.0.1:{port}",
                EnableLan302Proxy = true
            };

            var instance = new FrpTunnelInstance(config, NullLogger<FrpTunnelInstance>.Instance);

            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/vod/strm-source");
            req.Headers.TryAddWithoutValidation("Range", "bytes=1531576320-");
            req.Headers.TryAddWithoutValidation("User-Agent", "PlexMediaPlayer/1.0");

            var (resp, followedLan, finalUrl) = await instance.ExecuteLocalHttpWith302Async(
                req,
                $"http://127.0.0.1:{port}/vod/strm-source",
                "GET",
                null,
                cts.Token,
                "test-canonical-key");

            using (resp)
            {
                Assert.True(followedLan, "应当成功代拉局域网 302 私网直链");
                Assert.Equal(HttpStatusCode.PartialContent, resp.StatusCode);
                Assert.Equal("bytes=1531576320-", receivedRange);
                Assert.Equal("PlexMediaPlayer/1.0", receivedUa);
                Assert.EndsWith("/internal/strm-stream", finalUrl);
            }
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            try { await serverTask; } catch { }
        }
    }

    [Fact]
    public async Task TunnelRequestSession_FlowControl_And_Backpressure()
    {
        using var cts = new CancellationTokenSource();
        using var session = new TunnelRequestSession("req-fc", "GET", "/test", cts.Token);

        // 初始信用额度为 4MB
        Assert.Equal(4 * 1024 * 1024, session.Credit);

        // 起播突发免限流 (前 16MB) 消耗无需等待
        await session.ConsumeCreditAsync(2 * 1024 * 1024, cts.Token);
        Assert.Equal(2 * 1024 * 1024, session.Credit);

        // 增加信用额度 (HTTP_ACK)
        session.AddCredit(1024 * 1024);
        Assert.Equal(3 * 1024 * 1024, session.Credit);

        // Abort 即时中断并取消 CTS
        session.Abort();
        Assert.Equal(TunnelRequestState.Aborted, session.State);
        Assert.True(session.Cts.IsCancellationRequested);
    }
}


