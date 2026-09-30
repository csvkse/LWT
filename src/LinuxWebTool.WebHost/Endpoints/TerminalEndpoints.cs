using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LinuxWebTool.Contracts.Terminal;
using LinuxWebTool.Infrastructure.Security;
using LinuxWebTool.Infrastructure.Terminal;
using LinuxWebTool.WebHost.Composition;
using Microsoft.IdentityModel.JsonWebTokens;

namespace LinuxWebTool.WebHost.Endpoints;

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
            if (!isAuthenticated)
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

            using var webSocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);

            var sendTask = Task.Run(async () =>
            {
                var outBuffer = new byte[4096];
                var charBuffer = new char[4096];
                var decoder = Encoding.UTF8.GetDecoder();
                try
                {
                    while (!cts.IsCancellationRequested && webSocket.State == WebSocketState.Open)
                    {
                        var read = await session.StandardOutput.ReadAsync(outBuffer.AsMemory(0, outBuffer.Length), cts.Token).ConfigureAwait(false);
                        if (read <= 0)
                        {
                            if (session.HasExited)
                            {
                                cts.Cancel();
                                break;
                            }
                            await Task.Delay(50, cts.Token).ConfigureAwait(false);
                            continue;
                        }

                        var charCount = decoder.GetChars(outBuffer, 0, read, charBuffer, 0, flush: false);
                        if (charCount > 0)
                        {
                            var textBytes = Encoding.UTF8.GetBytes(charBuffer, 0, charCount);
                            await webSocket.SendAsync(textBytes.AsMemory(), WebSocketMessageType.Text, true, cts.Token).ConfigureAwait(false);
                        }
                    }

                    if (webSocket.State == WebSocketState.Open)
                    {
                        var remainingChars = decoder.GetChars([], 0, 0, charBuffer, 0, flush: true);
                        if (remainingChars > 0)
                        {
                            var textBytes = Encoding.UTF8.GetBytes(charBuffer, 0, remainingChars);
                            await webSocket.SendAsync(textBytes.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                }
                catch { }
            }, cts.Token);

            var inBuffer = new byte[4096];
            try
            {
                while (webSocket.State == WebSocketState.Open && !cts.IsCancellationRequested)
                {
                    var result = await webSocket.ReceiveAsync(inBuffer.AsMemory(0, inBuffer.Length), cts.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (result.Count > 0)
                    {
                        var text = Encoding.UTF8.GetString(inBuffer, 0, result.Count);
                        if (text.StartsWith("{\"type\":\"resize\"", StringComparison.Ordinal))
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(text);
                                var root = doc.RootElement;
                                var cols = root.GetProperty("cols").GetInt32();
                                var rows = root.GetProperty("rows").GetInt32();
                                await session.ResizeAsync(cols, rows, cts.Token).ConfigureAwait(false);
                                continue;
                            }
                            catch { }
                        }

                        ReadOnlyMemory<byte> toWrite = inBuffer.AsMemory(0, result.Count);
                        if (OperatingSystem.IsWindows() && session is not WindowsConPtySession)
                        {
                            var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
                            toWrite = Encoding.UTF8.GetBytes(normalized);
                        }
                        await session.StandardInput.WriteAsync(toWrite, cts.Token).ConfigureAwait(false);
                        await session.StandardInput.FlushAsync(cts.Token).ConfigureAwait(false);
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
                    try { await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None).ConfigureAwait(false); }
                    catch { }
                }
            }
        });

        return app;
    }
}
