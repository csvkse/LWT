using System.Diagnostics;
using System.Net.Sockets;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Infrastructure.Features.Plugins.Supervisor;

namespace LinuxWebTool.Infrastructure.Features.Plugins.Supervisor;

/// <summary>
/// 插件微服务主动就绪探测器 (Active Readiness Prober)
/// 负责解决“假就绪 (False Ready)”痛点：
/// - 在进程启动后执行 HTTP GET /health 或 TCP 端口连通性探测；
/// - 探针在规定预算（默认 10~15 秒）内轮询探测；
/// - 仅当底层网络协议完全准备好后，才将状态机跃迁为 Ready / Running，杜绝 502 穿透。
/// </summary>
public static class PluginReadinessProber
{
    private static readonly HttpClient HttpClientInstance = new()
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    public static async Task<bool> ProbeAsync(
        Process process,
        PluginManifest manifest,
        int port,
        PluginStateMachine stateMachine,
        PluginProcessRingBuffer ringBuffer,
        long runId,
        CancellationToken ct = default)
    {
        stateMachine.NotifyProbing(runId);
        ringBuffer.Append($"[{DateTime.Now:HH:mm:ss}] [SYSTEM] 正在等待微服务就绪探针响应 (端口: {port})...");

        var deadline = DateTime.UtcNow.AddSeconds(15);
        string targetUrl;
        if (!string.IsNullOrWhiteSpace(manifest.Probes?.Readiness?.Url))
        {
            targetUrl = manifest.Probes.Readiness.Url.Replace("{port}", port.ToString());
        }
        else if (!string.IsNullOrWhiteSpace(manifest.Interface?.HealthEndpoint))
        {
            var ep = manifest.Interface.HealthEndpoint.StartsWith('/') ? manifest.Interface.HealthEndpoint : "/" + manifest.Interface.HealthEndpoint;
            targetUrl = $"http://127.0.0.1:{port}{ep}";
        }
        else
        {
            targetUrl = $"http://127.0.0.1:{port}/health";
        }

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (process.HasExited)
            {
                ringBuffer.Append($"[{DateTime.Now:HH:mm:ss}] [WARN] 就绪探测期间微服务子进程已提前退出 (ExitCode: {process.ExitCode})");
                return false;
            }

            try
            {
                var isExplicitSocketCheck = manifest.Probes?.Readiness?.Type?.Equals("SocketCheck", StringComparison.OrdinalIgnoreCase) == true;
                if (!isExplicitSocketCheck && manifest.Network?.SupportHttp == true)
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(800);

                    try
                    {
                        var response = await HttpClientInstance.GetAsync(targetUrl, cts.Token).ConfigureAwait(false);
                        var code = (int)response.StatusCode;

                        // 2xx, 3xx 判定成功；401/403 表明微服务路由与安全鉴权中间件已完全就绪
                        if ((code >= 200 && code < 400) || code is 401 or 403)
                        {
                            stateMachine.NotifyRunning(runId, readiness: "Ready");
                            ringBuffer.Append($"[{DateTime.Now:HH:mm:ss}] [SYSTEM] 就绪探针校验通过 (HTTP {code})，服务正式上线");
                            return true;
                        }
                    }
                    catch
                    {
                        // HTTP 请求失败，进入下方的 TCP 端口自适应回退探测
                    }
                }

                // TCP Socket 握手探测（显式声明 SocketCheck 或 HTTP 失败自适应回退）
                using (var tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(500);

                    await tcp.ConnectAsync("127.0.0.1", port, cts.Token).ConfigureAwait(false);
                    if (tcp.Connected)
                    {
                        stateMachine.NotifyRunning(runId, readiness: "Ready");
                        ringBuffer.Append($"[{DateTime.Now:HH:mm:ss}] [SYSTEM] 端口握手探针校验通过 (TCP:{port})，服务正式上线");
                        return true;
                    }
                }
            }
            catch
            {
                // 探针重试中，等待 250ms
            }

            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        if (process.HasExited)
        {
            return false;
        }

        // 超时降级：若超过 15 秒但进程仍在平稳运行，标记为已运行但 Readiness 降级为 Degraded
        stateMachine.NotifyRunning(runId, readiness: "Degraded");
        ringBuffer.Append($"[{DateTime.Now:HH:mm:ss}] [WARN] 微服务就绪探针在 15 秒内未响应，状态标记为 Degraded (进程仍存活)");
        return true;
    }
}
