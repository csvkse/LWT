using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LinuxWebTool.WebHost.Composition;
using Microsoft.IdentityModel.JsonWebTokens;
namespace LinuxWebTool.WebHost.Features.Terminal.Endpoints;

public static class TerminalEndpoints
{
    public static IEndpointRouteBuilder MapTerminalEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/terminal/ws/{sessionId}", async (string sessionId, HttpContext context, IPtySessionManager pty, JwtIssuer jwtIssuer) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            // WebSocket 握手鉴权：优先检查已认证的用户，其次检查 Query Token
            var isAuthenticated = context.User?.Identity?.IsAuthenticated == true;
            var ticket = context.Request.Query["ticket"].ToString();
            if (!string.IsNullOrEmpty(ticket)) isAuthenticated = pty.ConsumeAttachmentTicket(sessionId, ticket);
            if (!isAuthenticated && string.IsNullOrEmpty(ticket))
            {
                var tokenQuery = context.Request.Query["token"].ToString();
                if (!string.IsNullOrWhiteSpace(tokenQuery))
                {
                    try
                    {
                        var handler = new JsonWebTokenHandler();
                        var result = await handler.ValidateTokenAsync(tokenQuery, jwtIssuer.BuildValidationParameters()).ConfigureAwait(false);
                        isAuthenticated = result.IsValid;
                    }
                    catch
                    {
                        isAuthenticated = false;
                    }
                }
            }

            if (!isAuthenticated)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var session = pty.GetSession(sessionId);
            if (session is null || session.HasExited)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var framed = context.Request.Query["v"] == "2";
            var after = long.TryParse(context.Request.Query["after"], out var requestedSequence) ? Math.Max(0, requestedSequence) : 0;
            IPtyAttachment? attached;
            try { attached = pty.Attach(sessionId, after); }
            catch (InvalidOperationException)
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                return;
            }
            if (attached is null) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
            await using var attachment = attached;
            using var webSocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);

            var sendTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var frame in attachment.ReadAllAsync(cts.Token).ConfigureAwait(false))
                    {
                        var text = framed ? JsonSerializer.Serialize(frame, AppJsonSerializerContext.Default.TerminalOutputFrame)
                            : frame.Truncated ? "\r\n[历史输出已截断]\r\n" : frame.Data;
                        await webSocket.SendAsync(Encoding.UTF8.GetBytes(text).AsMemory(), WebSocketMessageType.Text, true, cts.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or WebSocketException) { }
                finally { cts.Cancel(); }
            }, cts.Token);

            var inBuffer = new byte[4096];
            try
            {
                while (webSocket.State == WebSocketState.Open && !cts.IsCancellationRequested)
                {
                    using var message = new MemoryStream();
                    ValueWebSocketReceiveResult result;
                    do
                    {
                        result = await webSocket.ReceiveAsync(inBuffer.AsMemory(), cts.Token).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > 65536)
                            throw new WebSocketException("终端输入消息无效或过大");
                        message.Write(inBuffer, 0, result.Count);
                    } while (!result.EndOfMessage);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (message.Length > 0)
                    {
                        var text = Encoding.UTF8.GetString(message.ToArray());
                        if (framed || text.StartsWith("{\"type\":\"resize\"", StringComparison.Ordinal))
                        {
                            using var doc = JsonDocument.Parse(text);
                            var root = doc.RootElement;
                            var type = root.GetProperty("type").GetString();
                            if (type == "resize")
                            {
                                var cols = root.GetProperty("cols").GetInt32();
                                var rows = root.GetProperty("rows").GetInt32();
                                if (cols < 1 || cols > 500 || rows < 1 || rows > 200) continue;
                                await attachment.ResizeAsync(cols, rows, cts.Token).ConfigureAwait(false);
                                continue;
                            }
                            if (type != "input") continue;
                            text = root.GetProperty("data").GetString() ?? "";
                        }

                        ReadOnlyMemory<byte> toWrite = Encoding.UTF8.GetBytes(text);
                        if (OperatingSystem.IsWindows() && session is not WindowsConPtySession)
                        {
                            var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
                            toWrite = Encoding.UTF8.GetBytes(normalized);
                        }
                        await attachment.WriteInputAsync(toWrite, cts.Token).ConfigureAwait(false);
                    }
                }
            }
            catch { }
            finally
            {
                cts.Cancel();
                await sendTask.ConfigureAwait(false);
                if (webSocket.State == WebSocketState.Open)
                {
                    try { await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None).ConfigureAwait(false); }
                    catch { }
                }
                else if (webSocket.State == WebSocketState.CloseReceived)
                    await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "detached", CancellationToken.None).ConfigureAwait(false);
            }
        });

        return app;
    }
}
