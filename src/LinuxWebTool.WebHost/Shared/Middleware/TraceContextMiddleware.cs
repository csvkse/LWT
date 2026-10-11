using System.Diagnostics;
using System.Security.Cryptography;
using LinuxWebTool.Contracts.Features.SystemStatus.Contracts;

namespace LinuxWebTool.WebHost.Shared.Middleware;

/// <summary>
/// W3C TraceContext 与性能度量中间件。
/// 规范化解析/生成 traceparent 标头，注入 TraceId/SpanId，并记录接口耗时到 AppMetricsService。
/// </summary>
public sealed class TraceContextMiddleware(RequestDelegate next, IAppMetricsService metricsService)
{
    private const string TraceparentHeader = "traceparent";
    private const string XTraceIdHeader = "X-Trace-Id";
    private const string XCorrelationIdHeader = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var (traceId, spanId) = ResolveOrCreateTraceContext(context.Request.Headers[TraceparentHeader].ToString());

        context.Items["TraceId"] = traceId;
        context.Items["SpanId"] = spanId;

        var outgoingTraceparent = $"00-{traceId}-{spanId}-01";

        context.Response.Headers[TraceparentHeader] = outgoingTraceparent;
        context.Response.Headers[XTraceIdHeader] = traceId;
        context.Response.Headers[XCorrelationIdHeader] = traceId;

        metricsService.IncrementActiveRequests();
        var startTimestamp = Stopwatch.GetTimestamp();

        try
        {
            await next(context);
        }
        finally
        {
            metricsService.DecrementActiveRequests();
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            metricsService.RecordRequest(context.Request.Method, context.Response.StatusCode, elapsedMs);
        }
    }

    private static (string TraceId, string SpanId) ResolveOrCreateTraceContext(string? traceparent)
    {
        if (!string.IsNullOrWhiteSpace(traceparent))
        {
            var parts = traceparent.Trim().Split('-');
            if (parts.Length == 4 &&
                parts[0] == "00" &&
                parts[1].Length == 32 &&
                !IsAllZero(parts[1]) &&
                parts[2].Length == 16 &&
                !IsAllZero(parts[2]))
            {
                // 有效 W3C traceparent：沿用 traceId，生成本节点的新 spanId
                return (parts[1].ToLowerInvariant(), GenerateHexId(8));
            }
        }

        // 缺失或非规范标头：生成新的 16 字节 (32 hex) TraceId 与 8 字节 (16 hex) SpanId
        return (GenerateHexId(16), GenerateHexId(8));
    }

    private static string GenerateHexId(int byteCount)
    {
        Span<byte> bytes = stackalloc byte[byteCount];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }

    private static bool IsAllZero(string hex)
    {
        foreach (var c in hex)
        {
            if (c != '0') return false;
        }
        return true;
    }
}
