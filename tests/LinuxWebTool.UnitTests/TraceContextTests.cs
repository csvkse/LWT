using System.Text.RegularExpressions;
using LinuxWebTool.Contracts.Features.SystemStatus.Contracts;
using LinuxWebTool.WebHost.Shared.Middleware;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LinuxWebTool.UnitTests;

public sealed class TraceContextTests
{
    private sealed class MockMetricsService : IAppMetricsService
    {
        public int RecordCount { get; private set; }
        public int ActiveRequests { get; private set; }
        public string LastMethod { get; private set; } = string.Empty;
        public int LastStatus { get; private set; }
        public double LastElapsedMs { get; private set; }

        public void RecordRequest(string method, int statusCode, double elapsedMs)
        {
            RecordCount++;
            LastMethod = method;
            LastStatus = statusCode;
            LastElapsedMs = elapsedMs;
        }

        public void IncrementActiveRequests() => ActiveRequests++;
        public void DecrementActiveRequests() => ActiveRequests--;
        public void SetActiveTerminalSessions(int count) { }
        public AppMetricsSnapshot GetSnapshot() => new();
    }

    [Fact]
    public async Task TraceContextMiddleware_InjectsHeadersAndRecordsMetrics()
    {
        var metrics = new MockMetricsService();
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/api/test";

        var middleware = new TraceContextMiddleware(async ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            await Task.Yield();
        }, metrics);

        await middleware.InvokeAsync(context);

        Assert.True(context.Response.Headers.ContainsKey("traceparent"));
        Assert.True(context.Response.Headers.ContainsKey("X-Trace-Id"));
        Assert.True(context.Response.Headers.ContainsKey("X-Correlation-Id"));

        var traceparent = context.Response.Headers["traceparent"].ToString();
        var traceId = context.Response.Headers["X-Trace-Id"].ToString();

        Assert.Matches(@"^00-[0-9a-f]{32}-[0-9a-f]{16}-01$", traceparent);
        Assert.Matches(@"^[0-9a-f]{32}$", traceId);
        Assert.Contains(traceId, traceparent);
        Assert.Equal(traceId, context.Items["TraceId"]?.ToString());

        Assert.Equal(1, metrics.RecordCount);
        Assert.Equal("GET", metrics.LastMethod);
        Assert.Equal(200, metrics.LastStatus);
        Assert.True(metrics.LastElapsedMs >= 0);
        Assert.Equal(0, metrics.ActiveRequests);
    }

    [Fact]
    public async Task TraceContextMiddleware_InheritsValidIncomingTraceId()
    {
        var metrics = new MockMetricsService();
        var context = new DefaultHttpContext();
        const string incomingTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
        const string incomingSpanId = "00f067aa0ba902b7";
        context.Request.Headers["traceparent"] = $"00-{incomingTraceId}-{incomingSpanId}-01";

        var middleware = new TraceContextMiddleware(ctx => Task.CompletedTask, metrics);
        await middleware.InvokeAsync(context);

        var outgoingTraceparent = context.Response.Headers["traceparent"].ToString();
        var outgoingTraceId = context.Response.Headers["X-Trace-Id"].ToString();

        Assert.Equal(incomingTraceId, outgoingTraceId);
        Assert.StartsWith($"00-{incomingTraceId}-", outgoingTraceparent);
        // 子 SpanId 必须派生且不同于传入的 incomingSpanId
        Assert.DoesNotContain(incomingSpanId, outgoingTraceparent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid-format")]
    [InlineData("00-00000000000000000000000000000000-00f067aa0ba902b7-01")] // 全零 TraceId
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-0000000000000000-01")] // 全零 SpanId
    [InlineData("01-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01")] // 非 00 版本
    public async Task TraceContextMiddleware_GeneratesFreshIdsOnMalformedHeaders(string badHeader)
    {
        var metrics = new MockMetricsService();
        var context = new DefaultHttpContext();
        if (!string.IsNullOrEmpty(badHeader))
        {
            context.Request.Headers["traceparent"] = badHeader;
        }

        var middleware = new TraceContextMiddleware(ctx => Task.CompletedTask, metrics);
        await middleware.InvokeAsync(context);

        var traceId = context.Response.Headers["X-Trace-Id"].ToString();
        Assert.Matches(@"^[0-9a-f]{32}$", traceId);
        Assert.NotEqual(new string('0', 32), traceId);
    }
}
