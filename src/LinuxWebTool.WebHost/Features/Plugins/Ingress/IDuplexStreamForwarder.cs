using System.Net.WebSockets;

namespace LinuxWebTool.WebHost.Features.Plugins.Ingress;

/// <summary>
/// 平台级通用全双工流式转发抽象契约。
/// 适用于 WebSocket 隧道、双向 Raw TCP 劫持、VNC/WebSSH 二进制字节流以及长连接协议穿透。
/// </summary>
public interface IDuplexStreamForwarder
{
    Task ForwardWebSocketToTcpAsync(WebSocket webSocket, string host, int port, CancellationToken ct);
    Task ForwardStreamToTcpAsync(Stream clientStream, string host, int port, CancellationToken ct);
}
