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
}
