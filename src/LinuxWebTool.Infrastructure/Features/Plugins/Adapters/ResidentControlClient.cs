using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;

namespace LinuxWebTool.Infrastructure.Features.Plugins.Adapters;

/// <summary>
/// 原生常驻服务 HTTP 管理协议客户端：
/// - 仅使用本地回环 loopback 地址与每实例独立 Token 通信；
/// - 注入 runId 防止跨代次改投；
/// - 提供 handshake, health, invoke 与优雅 shutdown。
/// </summary>
public sealed class ResidentControlClient : IResidentControlClient
{
    private readonly HttpClient _httpClient;

    public ResidentControlClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<bool> HandshakeAsync(ResidentControlSpec spec, string runId, CancellationToken ct)
    {
        if (spec == null || spec.Port is null or <= 0) return false;

        var baseUrl = ResolveBaseUrl(spec);
        var url = $"{baseUrl}{spec.HandshakePath}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        AddControlHeaders(request, spec, runId);
        request.Content = JsonContent.Create(new { runId, timestamp = DateTimeOffset.UtcNow });

        try
        {
            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;

            var content = await ReadBoundedContentAsync(response.Content, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            if (root.TryGetProperty("protocolVersion", out var pVer) && pVer.GetInt32() == 1)
            {
                if (root.TryGetProperty("runId", out var pRun))
                {
                    return pRun.GetString() == runId;
                }
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string?> HealthAsync(ResidentControlSpec spec, string runId, CancellationToken ct)
    {
        if (spec == null || spec.Port is null or <= 0) return null;

        var baseUrl = ResolveBaseUrl(spec);
        var url = $"{baseUrl}{spec.HealthPath}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddControlHeaders(request, spec, runId);

        try
        {
            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return "Degraded";

            var content = await ReadBoundedContentAsync(response.Content, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("status", out var pStatus))
            {
                return pStatus.GetString() ?? "Ready";
            }
            return "Ready";
        }
        catch
        {
            return "Degraded";
        }
    }

    public async Task<PluginActionResult> InvokeAsync(
        ResidentControlSpec spec,
        string runId,
        string action,
        JsonElement payload,
        CancellationToken ct)
    {
        if (spec == null || spec.Port is null or <= 0)
        {
            return new PluginActionResult
            {
                Status = "failed",
                ErrorCode = "UnsupportedCapability",
                Error = "服务未声明或启用 HTTP 管理控制接口。"
            };
        }

        var baseUrl = ResolveBaseUrl(spec);
        var path = spec.InvokePath.EndsWith('/') ? $"{spec.InvokePath}{action}" : $"{spec.InvokePath}/{action}";
        var url = $"{baseUrl}{path}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        AddControlHeaders(request, spec, runId);
        request.Content = JsonContent.Create(payload);

        try
        {
            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            var content = await ReadBoundedContentAsync(response.Content, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new PluginActionResult
                {
                    Status = "failed",
                    ExitCode = (int)response.StatusCode,
                    Error = $"管理接口调用失败 HTTP {response.StatusCode}: {content}",
                    EffectUncertain = true
                };
            }

            using var doc = JsonDocument.Parse(content);
            return new PluginActionResult
            {
                Status = "succeeded",
                Data = doc.RootElement.Clone(),
                Message = "管理动作执行成功。"
            };
        }
        catch (OperationCanceledException)
        {
            return new PluginActionResult
            {
                Status = "cancelled",
                Error = "管理端点调用已取消。",
                EffectUncertain = true
            };
        }
        catch (Exception ex)
        {
            return new PluginActionResult
            {
                Status = "failed",
                ErrorCode = "HttpInvokeError",
                Error = $"管理端点连接异常: {ex.Message}",
                EffectUncertain = true
            };
        }
    }

    public async Task<bool> ShutdownAsync(ResidentControlSpec spec, string runId, TimeSpan timeout, CancellationToken ct)
    {
        if (spec == null || spec.Port is null or <= 0) return false;

        var baseUrl = ResolveBaseUrl(spec);
        var url = $"{baseUrl}{spec.ShutdownPath}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        AddControlHeaders(request, spec, runId);
        request.Content = JsonContent.Create(new { timeoutSeconds = timeout.TotalSeconds });

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            using var response = await _httpClient.SendAsync(request, linkedCts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private const int MaxResponseBytes = 4 * 1024 * 1024; // 4 MiB 保护上限

    private static async Task<string> ReadBoundedContentAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int totalRead = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            totalRead += read;
            if (totalRead > MaxResponseBytes)
            {
                throw new InvalidOperationException($"管理端点响应体积超过 {MaxResponseBytes} 字节安全上限。");
            }
            ms.Write(buffer, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string ResolveBaseUrl(ResidentControlSpec spec)
    {
        if (!string.IsNullOrEmpty(spec.BaseUrl))
        {
            if (!Uri.TryCreate(spec.BaseUrl, UriKind.Absolute, out var uri) || !uri.IsLoopback)
            {
                throw new System.Security.SecurityException($"常驻控制协议仅允许回环 loopback 地址，拒绝目标: '{spec.BaseUrl}'");
            }
            return spec.BaseUrl.TrimEnd('/');
        }
        return $"http://127.0.0.1:{spec.Port}";
    }

    private static void AddControlHeaders(HttpRequestMessage request, ResidentControlSpec spec, string runId)
    {
        request.Headers.Add("X-LWT-Run-Id", runId);
        if (!string.IsNullOrEmpty(spec.Token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", spec.Token);
            request.Headers.Add("X-LWT-Control-Token", spec.Token);
        }
    }
}
