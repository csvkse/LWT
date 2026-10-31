using System.Net;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Infrastructure.Features.Plugins.Adapters;
using Xunit;

namespace LinuxWebTool.UnitTests;

public sealed class ResidentControlClientTests
{
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Handler { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(Handler(request));
        }
    }

    [Fact]
    public async Task HandshakeAsync_ValidResponse_ReturnsTrueAndVerifiesHeaders()
    {
        string? capturedToken = null;
        string? capturedRunId = null;

        var handler = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                if (req.Headers.TryGetValues("X-LWT-Control-Token", out var tokens))
                    capturedToken = tokens.FirstOrDefault();
                if (req.Headers.TryGetValues("X-LWT-Run-Id", out var runIds))
                    capturedRunId = runIds.FirstOrDefault();

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"protocolVersion\": 1, \"status\": \"ready\", \"runId\": \"run-123\"}")
                };
            }
        };

        var httpClient = new HttpClient(handler);
        var client = new ResidentControlClient(httpClient);

        var spec = new ResidentControlSpec
        {
            Port = 18888,
            Token = "token-abc",
            HandshakePath = "/control/handshake"
        };

        var ok = await client.HandshakeAsync(spec, "run-123", CancellationToken.None);

        Assert.True(ok);
        Assert.Equal("token-abc", capturedToken);
        Assert.Equal("run-123", capturedRunId);
    }

    [Fact]
    public async Task HandshakeAsync_MismatchedRunId_ReturnsFalse()
    {
        var handler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"protocolVersion\": 1, \"status\": \"ready\", \"runId\": \"stale-run\"}")
            }
        };

        var client = new ResidentControlClient(new HttpClient(handler));
        var spec = new ResidentControlSpec { Port = 18888, HandshakePath = "/control/handshake" };

        var ok = await client.HandshakeAsync(spec, "active-run", CancellationToken.None);

        Assert.False(ok);
    }

    [Fact]
    public async Task HealthAsync_HealthyResponse_ReturnsStatus()
    {
        var handler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\": \"healthy\", \"uptimeSeconds\": 120}")
            }
        };

        var client = new ResidentControlClient(new HttpClient(handler));
        var spec = new ResidentControlSpec { Port = 18888, HealthPath = "/control/health" };

        var status = await client.HealthAsync(spec, "run-1", CancellationToken.None);

        Assert.Equal("healthy", status);
    }

    [Fact]
    public async Task InvokeAsync_SuccessfulResponse_ReturnsActionResult()
    {
        var handler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\": \"ok\", \"result\": {\"processed\": true}}")
            }
        };

        var client = new ResidentControlClient(new HttpClient(handler));
        var spec = new ResidentControlSpec { Port = 18888, InvokePath = "/control/invoke" };

        var payload = JsonDocument.Parse("{\"param\": 1}").RootElement;
        var result = await client.InvokeAsync(spec, "run-1", "test-act", payload, CancellationToken.None);

        Assert.Equal("succeeded", result.Status);
        Assert.NotNull(result.Data);
    }

    [Fact]
    public async Task ShutdownAsync_SuccessResponse_ReturnsTrue()
    {
        var handler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\": \"shutting_down\"}")
            }
        };

        var client = new ResidentControlClient(new HttpClient(handler));
        var spec = new ResidentControlSpec { Port = 18888, ShutdownPath = "/control/shutdown" };

        var ok = await client.ShutdownAsync(spec, "run-1", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(ok);
    }

    [Fact]
    public async Task InvokeAsync_NonLoopbackBaseUrl_ThrowsSecurityException()
    {
        var client = new ResidentControlClient(new HttpClient(new MockHttpMessageHandler()));
        var spec = new ResidentControlSpec
        {
            BaseUrl = "http://192.168.1.100:8080", // non-loopback
            Port = 8080,
            InvokePath = "/control/invoke"
        };

        var payload = JsonDocument.Parse("{}").RootElement;
        await Assert.ThrowsAsync<System.Security.SecurityException>(() =>
            client.InvokeAsync(spec, "run-1", "test", payload, CancellationToken.None));
    }

    [Fact]
    public async Task InvokeAsync_ResponseExceedsMaxLimit_ReturnsFailedResult()
    {
        var hugeData = new string('A', 5 * 1024 * 1024); // 5 MiB > 4 MiB
        var handler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(hugeData)
            }
        };

        var client = new ResidentControlClient(new HttpClient(handler));
        var spec = new ResidentControlSpec { Port = 18888, InvokePath = "/control/invoke" };

        var payload = JsonDocument.Parse("{}").RootElement;
        var result = await client.InvokeAsync(spec, "run-1", "test", payload, CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Equal("HttpInvokeError", result.ErrorCode);
    }
}
