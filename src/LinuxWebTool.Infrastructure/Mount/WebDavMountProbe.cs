using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LinuxWebTool.Infrastructure.Persistence.Entities;

namespace LinuxWebTool.Infrastructure.Mount;

public sealed record WebDavProbeResult(bool Success, bool RemoteAvailable, bool CredentialsRejected, string? Error);

/// <summary>远端探测绕过 FUSE 缓存；本地探测用独立进程防止请求线程被失效挂载卡住。</summary>
public sealed class WebDavMountProbe
{
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
    }) { Timeout = TimeSpan.FromSeconds(5) };

    public async Task<WebDavProbeResult> ProbeRemoteAsync(WebDavMount mount, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), mount.Url);
            request.Headers.TryAddWithoutValidation("Depth", "0");
            if (!string.IsNullOrEmpty(mount.Username))
            {
                var credential = Convert.ToBase64String(Encoding.UTF8.GetBytes(mount.Username + ":" + mount.Password));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credential);
            }
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode == 207) return new(true, true, false, null);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new(false, true, true, $"WebDAV 认证或权限失败（HTTP {(int)response.StatusCode}）");
            return new(false, false, false, $"WebDAV PROPFIND 返回 HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new(false, false, false, ex is TaskCanceledException ? "WebDAV 远端探测超时" : "WebDAV 远端连接或 TLS 验证失败");
        }
    }

    public Task<(bool Success, string? Error)> ProbeLocalAsync(WebDavMount mount, CancellationToken cancellationToken = default) =>
        SmbMountRuntimeProbe.RunFsProbeAsync(
            ["ls", "-A", "--", mount.LocalPath], TimeSpan.FromSeconds(5),
            "WebDAV 本地目录探测超时", cancellationToken);
}
