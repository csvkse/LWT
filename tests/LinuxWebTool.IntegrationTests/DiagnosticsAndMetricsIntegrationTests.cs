using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using LinuxWebTool.Contracts.Features.SystemStatus.Contracts;
using LinuxWebTool.IntegrationTests.Support;
using Xunit;

namespace LinuxWebTool.IntegrationTests;

public sealed class DiagnosticsAndMetricsIntegrationTests
{
    [Fact]
    public async Task Every_Response_Includes_W3C_Traceparent_And_TraceId()
    {
        var client = await TestServerFixture.CreateAnonymousClientAsync();
        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("traceparent"));
        Assert.True(response.Headers.Contains("X-Trace-Id"));
        Assert.True(response.Headers.Contains("X-Correlation-Id"));

        var traceparent = response.Headers.GetValues("traceparent").First();
        var traceId = response.Headers.GetValues("X-Trace-Id").First();

        Assert.Matches(@"^00-[0-9a-f]{32}-[0-9a-f]{16}-01$", traceparent);
        Assert.Matches(@"^[0-9a-f]{32}$", traceId);
        Assert.Contains(traceId, traceparent);
    }

    [Fact]
    public async Task Incoming_Traceparent_Is_Propagated_With_Child_Span()
    {
        var client = await TestServerFixture.CreateAnonymousClientAsync();
        const string clientTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
        const string clientSpanId = "00f067aa0ba902b7";
        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("traceparent", $"00-{clientTraceId}-{clientSpanId}-01");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var outgoingTraceId = response.Headers.GetValues("X-Trace-Id").First();
        var outgoingTraceparent = response.Headers.GetValues("traceparent").First();

        Assert.Equal(clientTraceId, outgoingTraceId);
        Assert.StartsWith($"00-{clientTraceId}-", outgoingTraceparent);
        Assert.DoesNotContain(clientSpanId, outgoingTraceparent);
    }

    [Fact]
    public async Task Unauthorized_Request_Still_Returns_Trace_Headers()
    {
        var client = await TestServerFixture.CreateAnonymousClientAsync();
        var response = await client.GetAsync("/api/Commands");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.Contains("traceparent"));
        Assert.True(response.Headers.Contains("X-Trace-Id"));

        var traceparent = response.Headers.GetValues("traceparent").First();
        Assert.Matches(@"^00-[0-9a-f]{32}-[0-9a-f]{16}-01$", traceparent);
    }

    [Fact]
    public async Task Metrics_Endpoint_Reflects_Live_Request_Traffic()
    {
        var client = await TestServerFixture.CreateAnonymousClientAsync();

        // 触发至少一次成功和一次 401 失败请求
        await client.GetAsync("/health");
        await client.GetAsync("/api/Commands"); // 401

        var metricsResp = await client.GetAsync("/api/Metrics");
        Assert.Equal(HttpStatusCode.OK, metricsResp.StatusCode);

        var json = await metricsResp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("totalRequests").GetInt64() >= 2);
        Assert.True(root.GetProperty("status2xx").GetInt64() >= 1);
        Assert.True(root.GetProperty("status4xx").GetInt64() >= 1);
        Assert.True(root.GetProperty("averageDurationMs").GetDouble() >= 0);
        Assert.True(root.GetProperty("uptimeSeconds").GetInt64() >= 0);
    }
}
