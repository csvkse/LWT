using System.Buffers;
using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.WebHost.Features.Plugins.Ingress;

/// <summary>
/// 高性能全双工双向流式转发器。
/// 采用 64KB 内存池（ArrayPool）与异步对拷泵（Bidirectional Stream Pump），实现零 GC 分配的高并发流转。
/// </summary>
public sealed class DuplexStreamForwarder(ILogger<DuplexStreamForwarder> logger) : IDuplexStreamForwarder
{
    private const int BufferSize = 65536; // 64KB

    public async Task ForwardWebSocketToTcpAsync(WebSocket webSocket, string host, int port, CancellationToken ct)
    {
        using var tcpClient = new TcpClient();
        tcpClient.NoDelay = true;

        try
        {
            await tcpClient.ConnectAsync(host, port, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[DuplexForwarder] 无法连接微服务后端目标 {Host}:{Port}", host, port);
            if (webSocket.State == WebSocketState.Open)
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "Backend unavailable", CancellationToken.None);
            }
            return;
        }

        using var networkStream = tcpClient.GetStream();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // 泵 1: WebSocket 入站流 -> 目标微服务 TCP 套接字
        var wsToTcp = Task.Run(async () =>
        {
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                while (!linkedCts.IsCancellationRequested && webSocket.State == WebSocketState.Open)
                {
                    var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), linkedCts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                        {
                            await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closed", linkedCts.Token);
                        }
                        break;
                    }

                    if (result.Count > 0)
                    {
                        await networkStream.WriteAsync(buffer.AsMemory(0, result.Count), linkedCts.Token);
                        await networkStream.FlushAsync(linkedCts.Token);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[DuplexForwarder] WebSocket->TCP 传输关闭");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                try { linkedCts.Cancel(); } catch { }
            }
        }, linkedCts.Token);

        // 泵 2: 目标微服务 TCP 套接字 -> WebSocket 出站流
        var tcpToWs = Task.Run(async () =>
        {
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                while (!linkedCts.IsCancellationRequested && webSocket.State == WebSocketState.Open)
                {
                    var bytesRead = await networkStream.ReadAsync(buffer.AsMemory(0, buffer.Length), linkedCts.Token);
                    if (bytesRead == 0)
                    {
                        if (webSocket.State == WebSocketState.Open)
                        {
                            await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Backend EOF", CancellationToken.None);
                        }
                        break;
                    }

                    await webSocket.SendAsync(
                        new ArraySegment<byte>(buffer, 0, bytesRead),
                        WebSocketMessageType.Binary,
                        endOfMessage: true,
                        linkedCts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[DuplexForwarder] TCP->WebSocket 传输关闭");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                try { linkedCts.Cancel(); } catch { }
            }
        }, linkedCts.Token);

        await Task.WhenAny(wsToTcp, tcpToWs);
        try { linkedCts.Cancel(); } catch { }
        try { await Task.WhenAll(wsToTcp, tcpToWs); } catch { }
    }

    public async Task ForwardStreamToTcpAsync(Stream clientStream, string host, int port, CancellationToken ct)
    {
        using var tcpClient = new TcpClient();
        tcpClient.NoDelay = true;

        try
        {
            await tcpClient.ConnectAsync(host, port, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[DuplexForwarder] 无法连接微服务后端目标 {Host}:{Port}", host, port);
            return;
        }

        using var networkStream = tcpClient.GetStream();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var t1 = clientStream.CopyToAsync(networkStream, BufferSize, linkedCts.Token);
        var t2 = networkStream.CopyToAsync(clientStream, BufferSize, linkedCts.Token);

        await Task.WhenAny(t1, t2);
        try { linkedCts.Cancel(); } catch { }
        try { await Task.WhenAll(t1, t2); } catch { }
    }
}
