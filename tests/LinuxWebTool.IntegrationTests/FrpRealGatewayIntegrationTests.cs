using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LinuxWebTool.Infrastructure.Persistence.Entities;
using LinuxWebTool.Infrastructure.Tunnel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinuxWebTool.IntegrationTests;

public sealed class FrpRealGatewayIntegrationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly Lazy<Dictionary<string, string>> EnvConfig = new(LoadEnvFile);

    private static Dictionary<string, string> LoadEnvFile()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 从当前工作目录或上层目录寻找 .env
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        string? envPath = null;
        for (var i = 0; i < 6 && dir != null; i++)
        {
            var candidate = Path.Combine(dir.FullName, ".env");
            if (File.Exists(candidate))
            {
                envPath = candidate;
                break;
            }
            dir = dir.Parent;
        }

        if (envPath == null || !File.Exists(envPath))
        {
            return dict;
        }

        foreach (var line in File.ReadAllLines(envPath))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#')) continue;

            var eqIdx = trimmed.IndexOf('=');
            if (eqIdx <= 0) continue;

            var key = trimmed[..eqIdx].Trim();
            var val = trimmed[(eqIdx + 1)..].Trim();
            if (val.StartsWith('"') && val.EndsWith('"') && val.Length >= 2)
            {
                val = val[1..^1];
            }
            dict[key] = val;
        }

        return dict;
    }

    [Fact]
    public void BuildWebSocketUri_Generates_Correct_ProxyByCF_Endpoint()
    {
        var uri = FrpTunnelInstance.BuildWebSocketUri("https://gateway.example.com", "lwt", "test_key");
        Assert.Equal("wss", uri.Scheme);
        Assert.Equal("gateway.example.com", uri.Host);
        Assert.Equal("/tunnel/connect", uri.AbsolutePath);
        Assert.Contains("host=lwt", uri.Query);
        Assert.Contains("key=test_key", uri.Query);
        Assert.Contains("token=test_key", uri.Query);
    }

    [Fact]
    public async Task Diagnostic_Dns_And_Tcp_Connect()
    {
        var env = EnvConfig.Value;
        if (!env.TryGetValue("FRP_SERVER_URL", out var serverUrl) || string.IsNullOrWhiteSpace(serverUrl))
        {
            return;
        }

        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var parsedUri)) return;
        var targetHost = parsedUri.Host;

        var ips = await System.Net.Dns.GetHostAddressesAsync(targetHost);
        foreach (var ip in ips)
        {
            using var socket = new System.Net.Sockets.Socket(ip.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await socket.ConnectAsync(new System.Net.IPEndPoint(ip, 443), cts.Token);
                sw.Stop();
                output.WriteLine($"SUCCESS {ip} in {sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                sw.Stop();
                output.WriteLine($"FAILED {ip} in {sw.ElapsedMilliseconds}ms: {ex.Message}");
            }
        }
    }

    [Fact]
    public async Task Live_Gateway_WebSocket_Handshake_And_PingPong_Succeeds()
    {
        var env = EnvConfig.Value;
        if (!env.TryGetValue("FRP_SERVER_URL", out var serverUrl) || string.IsNullOrWhiteSpace(serverUrl))
        {
            // 若环境无 .env 则跳过真实网络测试
            return;
        }

        env.TryGetValue("FRP_API_KEY", out var apiKey);
        env.TryGetValue("FRP_TUNNEL_HOST", out var tunnelHost);
        tunnelHost = string.IsNullOrWhiteSpace(tunnelHost) ? "lwt" : tunnelHost;

        var wsUri = FrpTunnelInstance.BuildWebSocketUri(serverUrl, tunnelHost, apiKey);

        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            ws.Options.SetRequestHeader("x-pyw-token", apiKey.Trim());
        }

        var handler = new SocketsHttpHandler
        {
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            ConnectCallback = async (context, cancellationToken) =>
            {
                var entry = await System.Net.Dns.GetHostEntryAsync(context.DnsEndPoint.Host, cancellationToken);
                var addresses = entry.AddressList
                    .OrderBy(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 0 : 1)
                    .ToArray();

                System.Net.Sockets.Socket? socket = null;
                foreach (var ip in addresses)
                {
                    socket = new System.Net.Sockets.Socket(ip.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                    socket.NoDelay = true;
                    try
                    {
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        linkedCts.CancelAfter(TimeSpan.FromSeconds(5));
                        await socket.ConnectAsync(new System.Net.IPEndPoint(ip, context.DnsEndPoint.Port), linkedCts.Token);
                        return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        socket = null;
                    }
                }

                throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostUnreachable);
            }
        };

        using var invoker = new HttpMessageInvoker(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // 1. 握手连接
        await ws.ConnectAsync(wsUri, invoker, cts.Token);
        Assert.Equal(WebSocketState.Open, ws.State);

        // 2. 接收网关就绪问候帧 (TUNNEL_CONNECTED)
        var buffer = new byte[8192];
        var receiveResult = await ws.ReceiveAsync(buffer, cts.Token);
        Assert.Equal(WebSocketMessageType.Text, receiveResult.MessageType);

        var greetingJson = Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);
        using (var doc = JsonDocument.Parse(greetingJson))
        {
            var type = doc.RootElement.GetProperty("type").GetString();
            Assert.Equal("TUNNEL_CONNECTED", type);
            var host = doc.RootElement.GetProperty("host").GetString();
            Assert.Equal(tunnelHost, host);
        }

        // 3. 发送心跳 Ping
        var pingBytes = Encoding.UTF8.GetBytes("{\"type\":\"ping\"}");
        await ws.SendAsync(pingBytes, WebSocketMessageType.Text, true, cts.Token);

        // 4. 接收心跳 Pong
        var pongResult = await ws.ReceiveAsync(buffer, cts.Token);
        Assert.Equal(WebSocketMessageType.Text, pongResult.MessageType);
        var pongJson = Encoding.UTF8.GetString(buffer, 0, pongResult.Count);
        using (var doc = JsonDocument.Parse(pongJson))
        {
            var type = doc.RootElement.GetProperty("type").GetString();
            Assert.Equal("pong", type);
        }

        // 5. 优雅关闭
        try
        {
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Test done", cts.Token);
        }
        catch { }
    }

    [Fact]
    public async Task Live_FrpTunnelInstance_Runs_Lifecycle_Cleanly()
    {
        var env = EnvConfig.Value;
        if (!env.TryGetValue("FRP_SERVER_URL", out var serverUrl) || string.IsNullOrWhiteSpace(serverUrl))
        {
            return;
        }

        env.TryGetValue("FRP_API_KEY", out var apiKey);
        env.TryGetValue("FRP_TUNNEL_HOST", out var tunnelHost);
        env.TryGetValue("FRP_LOCAL_TARGET_URL", out var localTarget);

        var entity = new FrpTunnelLineEntity
        {
            Id = "real_live_test",
            Name = "Real Gateway Line",
            ServerUrl = serverUrl,
            TunnelHost = string.IsNullOrWhiteSpace(tunnelHost) ? "lwt" : tunnelHost,
            ApiKey = apiKey ?? string.Empty,
            LocalTargetUrl = string.IsNullOrWhiteSpace(localTarget) ? "http://127.0.0.1:8080" : localTarget,
            AutoStart = false,
            HeartbeatIntervalSeconds = 15,
            EnableLan302Proxy = true,
            ProxyType = "Direct",
            Status = "Disconnected"
        };

        var instance = new FrpTunnelInstance(entity, NullLogger.Instance);

        try
        {
            await instance.StartAsync();

            // 等待连接建立 (最多 15 秒)
            var startWait = DateTime.UtcNow;
            while (instance.State != "Connected" && (DateTime.UtcNow - startWait).TotalSeconds < 15)
            {
                await Task.Delay(200);
            }

            Assert.Equal("Connected", instance.State);
            Assert.NotNull(instance.PublicUrl);
            Assert.Contains("/tunnel/", instance.PublicUrl);
        }
        finally
        {
            await instance.StopAsync();
            Assert.Equal("Stopped", instance.State);
        }
    }
}
