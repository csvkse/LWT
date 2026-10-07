using System.Collections.Concurrent;
using System.Text.Json;
using LinuxWebTool.WebHost.Composition;
namespace LinuxWebTool.WebHost.Features.Mcp.Endpoints;

/// <summary>
/// MCP SSE 与 Streamable HTTP JSON-RPC 路由端点
/// </summary>
public static class McpEndpoints
{
    private static readonly ConcurrentDictionary<string, McpSessionContext> Sessions = new();

    public static IEndpointRouteBuilder MapMcpEndpoints(this IEndpointRouteBuilder app)
    {
        // 1. SSE 握手端点: GET /mcp/sse
        app.MapGet("/mcp/sse", async (HttpContext context, McpServerEngine engine) =>
        {
            var callerKey = context.Items["CurrentApiKey"] as ApiKeyEntity;
            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";

            var sessionId = Guid.NewGuid().ToString("N");
            var session = new McpSessionContext(sessionId, callerKey);
            Sessions[sessionId] = session;

            // 发送握手 endpoint 事件，指示客户端后续发消息至 /mcp/message?sessionId=...
            var endpointUrl = $"/mcp/message?sessionId={sessionId}";
            await context.Response.WriteAsync($"event: endpoint\r\ndata: {endpointUrl}\r\n\r\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);

            try
            {
                // 维持 SSE 下行推送管道
                while (!context.RequestAborted.IsCancellationRequested)
                {
                    if (session.OutgoingMessages.TryDequeue(out var msgJson))
                    {
                        await context.Response.WriteAsync($"event: message\r\ndata: {msgJson}\r\n\r\n", context.RequestAborted);
                        await context.Response.Body.FlushAsync(context.RequestAborted);
                    }
                    else
                    {
                        await Task.Delay(100, context.RequestAborted);
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                Sessions.TryRemove(sessionId, out _);
            }
        });

        // 2. SSE 上行消息端点: POST /mcp/message
        app.MapPost("/mcp/message", async (HttpContext context, McpServerEngine engine) =>
        {
            var sessionId = context.Request.Query["sessionId"].ToString();
            if (string.IsNullOrWhiteSpace(sessionId) || !Sessions.TryGetValue(sessionId, out var session))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            using var reader = new StreamReader(context.Request.Body);
            var bodyText = await reader.ReadToEndAsync(context.RequestAborted);
            if (string.IsNullOrWhiteSpace(bodyText)) return;

            var rpcReq = JsonSerializer.Deserialize<McpRpcRequest>(bodyText, AppJsonSerializerContext.Default.Options);
            if (rpcReq == null)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var rpcResp = await engine.HandleRequestAsync(rpcReq, session.ApiKey, context);
            var respJson = JsonSerializer.Serialize(rpcResp, AppJsonSerializerContext.Default.Options);

            // 将响应推送到该 session 的下行 SSE 队列
            session.OutgoingMessages.Enqueue(respJson);
            context.Response.StatusCode = StatusCodes.Status202Accepted;
        });

        // 3. 直接 Streamable HTTP JSON-RPC 端点: POST /mcp
        app.MapPost("/mcp", async (HttpContext context, McpServerEngine engine) =>
        {
            var callerKey = context.Items["CurrentApiKey"] as ApiKeyEntity;

            using var reader = new StreamReader(context.Request.Body);
            var bodyText = await reader.ReadToEndAsync(context.RequestAborted);
            if (string.IsNullOrWhiteSpace(bodyText))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var rpcReq = JsonSerializer.Deserialize<McpRpcRequest>(bodyText, AppJsonSerializerContext.Default.Options);
            if (rpcReq == null)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var rpcResp = await engine.HandleRequestAsync(rpcReq, callerKey, context);
            context.Response.ContentType = "application/json; charset=utf-8";
            await JsonSerializer.SerializeAsync(context.Response.Body, rpcResp, AppJsonSerializerContext.Default.Options, context.RequestAborted);
        });

        return app;
    }

    private sealed class McpSessionContext(string sessionId, ApiKeyEntity? apiKey)
    {
        public string SessionId { get; } = sessionId;
        public ApiKeyEntity? ApiKey { get; } = apiKey;
        public ConcurrentQueue<string> OutgoingMessages { get; } = new();
    }
}
