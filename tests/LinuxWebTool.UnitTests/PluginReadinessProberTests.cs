using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Infrastructure.Features.Plugins.Supervisor;
using Xunit;

namespace LinuxWebTool.UnitTests;

public class PluginReadinessProberTests
{
    [Fact]
    public async Task ProbeAsync_SocketCheck_SucceedsOnListeningTcpPort()
    {
        // 1. 模拟一个纯 TCP 监听服务（类似 SOCKS5，无 HTTP 响应）
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var manifest = new PluginManifest
        {
            Id = "mock-socks5",
            Name = "Mock SOCKS5 Service",
            Profile = PluginProfile.Resident,
            Probes = new PluginProbeSpec
            {
                Readiness = new PluginProbeDefinition { Type = "SocketCheck", TimeoutSeconds = 2 }
            }
        };

        var sm = new PluginStateMachine(manifest.Id);
        sm.NotifyStarting(100);
        var ringBuffer = new PluginProcessRingBuffer();

        // 2. 探针应在 500ms 内建立 TCP 握手并标记为 Ready
        using var proc = Process.GetCurrentProcess();
        var success = await PluginReadinessProber.ProbeAsync(proc, manifest, port, sm, ringBuffer, 100);

        Assert.True(success);
        Assert.Equal(PluginState.Running, sm.State);
        Assert.Equal("Ready", sm.Readiness);
    }

    [Fact]
    public async Task ProbeAsync_HttpWith401Unauthorized_ConsideredHealthyAndReady()
    {
        // 模拟一个带有安全鉴权中间件的 HTTP 服务，返回 401
        using var httpListener = new HttpListener();
        var port = GetFreePort();
        httpListener.Prefixes.Add($"http://127.0.0.1:{port}/");
        httpListener.Start();

        _ = Task.Run(async () =>
        {
            try
            {
                var ctx = await httpListener.GetContextAsync();
                ctx.Response.StatusCode = 401; // 模拟未携带 token 返回 401
                ctx.Response.Close();
            }
            catch { }
        });

        var manifest = new PluginManifest
        {
            Id = "mock-auth-svc",
            Name = "Mock Auth Service",
            Profile = PluginProfile.Resident,
            Network = new PluginNetworkConfig { Mode = "port" },
            Probes = new PluginProbeSpec
            {
                Readiness = new PluginProbeDefinition { Type = "HttpCheck", TimeoutSeconds = 2 }
            }
        };

        var sm = new PluginStateMachine(manifest.Id);
        sm.NotifyStarting(200);
        var ringBuffer = new PluginProcessRingBuffer();

        using var proc = Process.GetCurrentProcess();
        var success = await PluginReadinessProber.ProbeAsync(proc, manifest, port, sm, ringBuffer, 200);

        Assert.True(success);
        Assert.Equal(PluginState.Running, sm.State);
        Assert.Equal("Ready", sm.Readiness);
    }

    [Fact]
    public async Task ProbeAsync_HttpFailsWithUnknownError_AdaptiveFallbackToTcpSocket()
    {
        // 模拟一个虽然配置了 HttpCheck，但实际上是裸 TCP 协议的服务（HTTP 请求会报错或超时）
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var manifest = new PluginManifest
        {
            Id = "mock-mismatch-svc",
            Name = "Mock Mismatch Service",
            Profile = PluginProfile.Resident,
            Network = new PluginNetworkConfig { Mode = "port" },
            Probes = new PluginProbeSpec
            {
                Readiness = new PluginProbeDefinition { Type = "HttpCheck", TimeoutSeconds = 2 }
            }
        };

        var sm = new PluginStateMachine(manifest.Id);
        sm.NotifyStarting(300);
        var ringBuffer = new PluginProcessRingBuffer();

        using var proc = Process.GetCurrentProcess();
        var success = await PluginReadinessProber.ProbeAsync(proc, manifest, port, sm, ringBuffer, 300);

        Assert.True(success);
        Assert.Equal(PluginState.Running, sm.State);
        Assert.Equal("Ready", sm.Readiness);
    }

    private static int GetFreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
