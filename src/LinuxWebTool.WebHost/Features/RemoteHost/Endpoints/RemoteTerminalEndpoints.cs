using System.Net.WebSockets;
using System.Text;
using LinuxWebTool.Infrastructure.Features.RemoteHost.Adapters;
using LinuxWebTool.Infrastructure.Features.Security.Adapters;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LinuxWebTool.WebHost.Features.RemoteHost.Endpoints;

public static class RemoteTerminalEndpoints
{
    public static IEndpointRouteBuilder MapRemoteTerminalEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/remote-hosts/{id}/terminal/ws/{sessionId}", async (
            string id,
            string sessionId,
            HttpContext context,
            RemoteTerminalSessionManager sessions,
            JwtIssuer jwtIssuer,
            AdminCredentialService adminCred) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            // 1. 验证身份
            var isAuthenticated = context.User?.Identity?.IsAuthenticated == true;
            if (!isAuthenticated)
            {
                var tokenQuery = context.Request.Query["token"].ToString();
                if (!string.IsNullOrWhiteSpace(tokenQuery))
                {
                    try
                    {
                        var result = await JwtIssuer.TokenHandler.ValidateTokenAsync(
                            tokenQuery,
                            jwtIssuer.BuildValidationParameters()).ConfigureAwait(false);

                        if (result.IsValid && result.ClaimsIdentity is not null)
                        {
                            var userName = result.ClaimsIdentity.FindFirst("sub")?.Value;
                            var stamp = result.ClaimsIdentity.FindFirst("stamp")?.Value;
                            isAuthenticated = !string.IsNullOrEmpty(userName)
                                && string.Equals(userName, adminCred.Account.UserName, StringComparison.Ordinal)
                                && adminCred.ValidateSecurityStamp(stamp);
                        }
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

            // 2. 获取活动终端会话
            var session = sessions.GetSession(sessionId);
            if (session is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            // 3. 接受 WebSocket 连接
            using var webSocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);

            // 发送任务：从 SSH 标准输出持续读取并泵送入 WebSocket
            var outputTask = Task.Run(async () =>
            {
                var outBuffer = new byte[4096];
                try
                {
                    while (webSocket.State == WebSocketState.Open && !cts.IsCancellationRequested)
                    {
                        var bytesRead = await session.StandardOutput.ReadAsync(outBuffer.AsMemory(), cts.Token).ConfigureAwait(false);
                        if (bytesRead == 0) break; // EOF

                        await webSocket.SendAsync(
                            outBuffer.AsMemory(0, bytesRead),
                            WebSocketMessageType.Text,
                            true,
                            cts.Token
                        ).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or WebSocketException) { }
                finally
                {
                    cts.Cancel();
                }
            }, cts.Token);

            // 接收任务：从 WebSocket 接收键盘输入并写入 SSH 标准输入
            var inBuffer = new byte[4096];
            try
            {
                while (webSocket.State == WebSocketState.Open && !cts.IsCancellationRequested)
                {
                    var result = await webSocket.ReceiveAsync(inBuffer.AsMemory(), cts.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None).ConfigureAwait(false);
                        break;
                    }

                    if (result.Count > 0)
                    {
                        await session.StandardInput.WriteAsync(inBuffer.AsMemory(0, result.Count), cts.Token).ConfigureAwait(false);
                        await session.StandardInput.FlushAsync(cts.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or WebSocketException) { }
            finally
            {
                cts.Cancel();
                try { await outputTask.ConfigureAwait(false); } catch { }
                await sessions.CloseSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            }
        });

        return app;
    }
}
