using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.Services.Contracts;
using LinuxWebTool.Contracts.Features.Shell.Contracts;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Services.Adapters.Docker;

/// <summary>
/// Docker 引擎原生适配器实现：
/// - 优先通过 SocketsHttpHandler 直接与 Docker Unix Socket (/var/run/docker.sock) 通信；
/// - Windows 环境直连 \\.\pipe\docker_engine 命名管道；
/// - Socket 通道不可用时自动降级使用 docker CLI；
/// - 完全 Native AOT 兼容，零外部重量级依赖。
/// </summary>
public sealed class DockerEngineAdapter : IDockerEngineAdapter
{
    private readonly IShellExecutor _shellExecutor;
    private readonly IServiceEnvironmentDetector _envDetector;
    private readonly ILogger<DockerEngineAdapter> _logger;
    private readonly HttpClient _httpClient;

    public DockerEngineAdapter(
        IShellExecutor shellExecutor,
        IServiceEnvironmentDetector envDetector,
        ILogger<DockerEngineAdapter> logger)
    {
        _shellExecutor = shellExecutor;
        _envDetector = envDetector;
        _logger = logger;

        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, cancellationToken) =>
            {
                if (OperatingSystem.IsWindows())
                {
                    const string pipeName = "docker_engine";
                    var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    await pipe.ConnectAsync(1500, cancellationToken);
                    return pipe;
                }
                else
                {
                    const string socketPath = "/var/run/docker.sock";
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    var endpoint = new UnixDomainSocketEndPoint(socketPath);
                    await socket.ConnectAsync(endpoint, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
            }
        };

        _httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost"),
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public async Task<IReadOnlyList<DockerContainerSummary>> ListContainersAsync(bool all = true, CancellationToken cancellationToken = default)
    {
        // 1. 尝试 Unix Socket / Named Pipe 直连
        try
        {
            var url = $"/containers/json?all={(all ? "true" : "false")}";
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                return ParseContainersJson(json);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Docker socket request failed, falling back to CLI");
        }

        // 2. 降级使用 docker CLI
        return await ListContainersCliAsync(all, cancellationToken);
    }

    public async Task<ServiceActionResult> ExecuteContainerActionAsync(DockerContainerActionRequest request, CancellationToken cancellationToken = default)
    {
        var id = request.ContainerId.Trim();
        if (string.IsNullOrWhiteSpace(id))
        {
            return new ServiceActionResult { Success = false, Message = "容器 ID 不能为空" };
        }

        // 尝试 Socket API
        try
        {
            HttpResponseMessage? res = null;
            if (request.Action == DockerContainerAction.Start)
            {
                res = await _httpClient.PostAsync($"/containers/{id}/start", null, cancellationToken);
            }
            else if (request.Action == DockerContainerAction.Stop)
            {
                res = await _httpClient.PostAsync($"/containers/{id}/stop?t=10", null, cancellationToken);
            }
            else if (request.Action == DockerContainerAction.Restart)
            {
                res = await _httpClient.PostAsync($"/containers/{id}/restart?t=10", null, cancellationToken);
            }
            else if (request.Action == DockerContainerAction.Pause)
            {
                res = await _httpClient.PostAsync($"/containers/{id}/pause", null, cancellationToken);
            }
            else if (request.Action == DockerContainerAction.Unpause)
            {
                res = await _httpClient.PostAsync($"/containers/{id}/unpause", null, cancellationToken);
            }
            else if (request.Action == DockerContainerAction.Kill)
            {
                res = await _httpClient.PostAsync($"/containers/{id}/kill", null, cancellationToken);
            }
            else if (request.Action == DockerContainerAction.Remove)
            {
                var v = request.RemoveVolumes ? "true" : "false";
                var f = request.Force ? "true" : "false";
                res = await _httpClient.DeleteAsync($"/containers/{id}?v={v}&force={f}", cancellationToken);
            }

            if (res != null)
            {
                if (res.IsSuccessStatusCode || (int)res.StatusCode == 204 || (int)res.StatusCode == 304)
                {
                    return new ServiceActionResult { Success = true, Message = $"容器 [{id}] 操作成功" };
                }
                var err = await res.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Docker action API returned {Code}: {Err}", res.StatusCode, err);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Docker action socket failed, falling back to CLI");
        }

        // CLI 降级
        return await ExecuteContainerActionCliAsync(request, cancellationToken);
    }

    public async Task<string> GetContainerLogsAsync(DockerLogsRequest request, CancellationToken cancellationToken = default)
    {
        var id = request.ContainerId.Trim();
        var tail = Math.Clamp(request.Tail, 10, 1000);

        try
        {
            var url = $"/containers/{id}/logs?stdout=true&stderr=true&tail={tail}&timestamps={(request.Timestamps ? "true" : "false")}";
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                return SanitizeDockerLogStream(bytes);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Docker logs socket failed, falling back to CLI");
        }

        var cmd = $"docker logs --tail {tail} {(request.Timestamps ? "-t" : "")} {id}";
        var res = await _shellExecutor.ExecuteAsync(new ShellRequest { CommandText = cmd, TimeoutSeconds = 10 }, cancellationToken);
        return string.IsNullOrEmpty(res.StandardOutput) ? res.ErrorOutput : res.StandardOutput;
    }

    public async Task<IReadOnlyList<DockerImageSummary>> ListImagesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync("/images/json", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                return ParseImagesJson(json);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Docker images socket failed, falling back to CLI");
        }

        var res = await _shellExecutor.ExecuteAsync(new ShellRequest
        {
            CommandText = "docker images --format \"{{json .}}\"",
            TimeoutSeconds = 8
        }, cancellationToken);

        if (res.ExitCode != 0 || string.IsNullOrWhiteSpace(res.StandardOutput))
        {
            return [];
        }

        var list = new List<DockerImageSummary>();
        var lines = res.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var l in lines)
        {
            try
            {
                using var doc = JsonDocument.Parse(l);
                var root = doc.RootElement;
                var repo = root.TryGetProperty("Repository", out var r) ? r.GetString() ?? "" : "";
                var tag = root.TryGetProperty("Tag", out var t) ? t.GetString() ?? "" : "";
                var id = root.TryGetProperty("ID", out var i) ? i.GetString() ?? "" : "";
                list.Add(new DockerImageSummary
                {
                    Id = id,
                    RepoTags = [string.IsNullOrEmpty(tag) ? repo : $"{repo}:{tag}"],
                    Size = 0,
                    Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });
            }
            catch { }
        }
        return list;
    }

    #region 内部辅助解析
    private static IReadOnlyList<DockerContainerSummary> ParseContainersJson(string json)
    {
        var list = new List<DockerContainerSummary>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var id = el.TryGetProperty("Id", out var pId) ? pId.GetString() ?? "" : "";
            var image = el.TryGetProperty("Image", out var pImg) ? pImg.GetString() ?? "" : "";
            var command = el.TryGetProperty("Command", out var pCmd) ? pCmd.GetString() : null;
            var created = el.TryGetProperty("Created", out var pCre) ? pCre.GetInt64() : 0;
            var state = el.TryGetProperty("State", out var pSt) ? pSt.GetString() ?? "unknown" : "unknown";
            var status = el.TryGetProperty("Status", out var pStat) ? pStat.GetString() : null;

            var names = new List<string>();
            if (el.TryGetProperty("Names", out var pNames) && pNames.ValueKind == JsonValueKind.Array)
            {
                foreach (var n in pNames.EnumerateArray())
                {
                    var s = n.GetString();
                    if (!string.IsNullOrEmpty(s)) names.Add(s.TrimStart('/'));
                }
            }

            var ports = new List<DockerPortMapping>();
            if (el.TryGetProperty("Ports", out var pPorts) && pPorts.ValueKind == JsonValueKind.Array)
            {
                foreach (var pt in pPorts.EnumerateArray())
                {
                    ports.Add(new DockerPortMapping
                    {
                        PrivatePort = pt.TryGetProperty("PrivatePort", out var pr) ? pr.GetInt32() : null,
                        PublicPort = pt.TryGetProperty("PublicPort", out var pu) ? pu.GetInt32() : null,
                        Type = pt.TryGetProperty("Type", out var ty) ? ty.GetString() : null,
                        Ip = pt.TryGetProperty("IP", out var ip) ? ip.GetString() : null
                    });
                }
            }

            list.Add(new DockerContainerSummary
            {
                Id = id,
                Names = names,
                Image = image,
                Command = command,
                Created = created,
                State = state,
                Status = status,
                Ports = ports
            });
        }
        return list;
    }

    private static IReadOnlyList<DockerImageSummary> ParseImagesJson(string json)
    {
        var list = new List<DockerImageSummary>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var id = el.TryGetProperty("Id", out var pId) ? pId.GetString() ?? "" : "";
            var size = el.TryGetProperty("Size", out var pSz) ? pSz.GetInt64() : 0;
            var created = el.TryGetProperty("Created", out var pCr) ? pCr.GetInt64() : 0;

            var tags = new List<string>();
            if (el.TryGetProperty("RepoTags", out var pTags) && pTags.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in pTags.EnumerateArray())
                {
                    var s = t.GetString();
                    if (!string.IsNullOrEmpty(s)) tags.Add(s);
                }
            }

            list.Add(new DockerImageSummary
            {
                Id = id,
                RepoTags = tags,
                Size = size,
                Created = created
            });
        }
        return list;
    }

    private async Task<IReadOnlyList<DockerContainerSummary>> ListContainersCliAsync(bool all, CancellationToken cancellationToken)
    {
        var flag = all ? "-a" : "";
        var res = await _shellExecutor.ExecuteAsync(new ShellRequest
        {
            CommandText = $"docker ps {flag} --format \"{{{{json .}}}}\"",
            TimeoutSeconds = 8
        }, cancellationToken);

        if (res.ExitCode != 0 || string.IsNullOrWhiteSpace(res.StandardOutput))
        {
            return [];
        }

        var list = new List<DockerContainerSummary>();
        var lines = res.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var l in lines)
        {
            try
            {
                using var doc = JsonDocument.Parse(l);
                var root = doc.RootElement;
                var id = root.TryGetProperty("ID", out var i) ? i.GetString() ?? "" : "";
                var names = root.TryGetProperty("Names", out var n) ? new[] { n.GetString() ?? "" } : Array.Empty<string>();
                var image = root.TryGetProperty("Image", out var img) ? img.GetString() ?? "" : "";
                var state = root.TryGetProperty("State", out var st) ? st.GetString() ?? "unknown" : "unknown";
                var status = root.TryGetProperty("Status", out var stat) ? stat.GetString() : null;

                list.Add(new DockerContainerSummary
                {
                    Id = id,
                    Names = names,
                    Image = image,
                    State = state,
                    Status = status
                });
            }
            catch { }
        }
        return list;
    }

    private async Task<ServiceActionResult> ExecuteContainerActionCliAsync(DockerContainerActionRequest request, CancellationToken cancellationToken)
    {
        var id = request.ContainerId.Trim();
        var verb = request.Action switch
        {
            DockerContainerAction.Start => "start",
            DockerContainerAction.Stop => "stop",
            DockerContainerAction.Restart => "restart",
            DockerContainerAction.Pause => "pause",
            DockerContainerAction.Unpause => "unpause",
            DockerContainerAction.Kill => "kill",
            DockerContainerAction.Remove => $"rm {(request.RemoveVolumes ? "-v" : "")} {(request.Force ? "-f" : "")}".Trim(),
            _ => "inspect"
        };

        var cmd = $"docker {verb} {id}";
        var res = await _shellExecutor.ExecuteAsync(new ShellRequest { CommandText = cmd, TimeoutSeconds = 15 }, cancellationToken);

        return new ServiceActionResult
        {
            Success = res.ExitCode == 0,
            Message = res.ExitCode == 0 ? $"容器 [{id}] 操作成功" : res.ErrorOutput,
            Output = res.StandardOutput
        };
    }

    /// <summary>
    /// Docker 多路复用日志帧头部剥离（每个数据块前 8 字节是 header：0=stdin, 1=stdout, 2=stderr + 4 字节大端长度）。
    /// </summary>
    private static string SanitizeDockerLogStream(byte[] bytes)
    {
        if (bytes.Length == 0) return "";
        try
        {
            using var ms = new MemoryStream(bytes);
            using var reader = new StreamReader(ms);
            var sb = new System.Text.StringBuilder();

            while (ms.Position < ms.Length)
            {
                // 如果剩余不足 8 字节，可能是非 multiplexed 裸流
                if (ms.Length - ms.Position < 8)
                {
                    sb.Append(reader.ReadToEnd());
                    break;
                }

                var header = new byte[8];
                var read = ms.Read(header, 0, 8);
                if (read < 8) break;

                // header[0] stream type: 1=stdout, 2=stderr
                // 大端字节序读取 payload 长度
                var size = (header[4] << 24) | (header[5] << 16) | (header[6] << 8) | header[7];
                if (size <= 0 || size > ms.Length - ms.Position)
                {
                    // 若长度异常，直接把剩余部分按普通文本读取
                    ms.Seek(-8, SeekOrigin.Current);
                    sb.Append(reader.ReadToEnd());
                    break;
                }

                var payload = new byte[size];
                ms.Read(payload, 0, size);
                sb.Append(System.Text.Encoding.UTF8.GetString(payload));
            }

            return sb.ToString();
        }
        catch
        {
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
    }
    #endregion
}
