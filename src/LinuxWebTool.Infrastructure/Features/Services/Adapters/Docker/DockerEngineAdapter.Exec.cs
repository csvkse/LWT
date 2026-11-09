using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinuxWebTool.Contracts.Features.Services.Contracts;
using LinuxWebTool.Contracts.Features.Shell.Contracts;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Services.Adapters.Docker;

[JsonSerializable(typeof(DockerCreateExecRequestPayload))]
[JsonSerializable(typeof(DockerStartExecRequestPayload))]
internal sealed partial class DockerExecJsonContext : JsonSerializerContext
{
}

internal sealed class DockerCreateExecRequestPayload
{
    public bool AttachStdout { get; set; } = true;
    public bool AttachStderr { get; set; } = true;
    public bool Tty { get; set; }
    public string[] Cmd { get; set; } = [];
    public string? WorkingDir { get; set; }
    public string? User { get; set; }
}

internal sealed class DockerStartExecRequestPayload
{
    public bool Detach { get; set; }
    public bool Tty { get; set; }
}

public sealed partial class DockerEngineAdapter
{
    #region 容器 Inspect / 命令执行 / Archive 文件归档
    public async Task<DockerContainerInspect?> InspectContainerAsync(string containerId, CancellationToken cancellationToken = default)
    {
        var id = (containerId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(id)) return null;

        try
        {
            using var response = await _httpClient.GetAsync($"/containers/{id}/json", cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var realId = root.TryGetProperty("Id", out var idElem) ? idElem.GetString() ?? id : id;
            var name = root.TryGetProperty("Name", out var n) ? (n.GetString()?.TrimStart('/') ?? "") : "";

            int pid = 0;
            bool running = false, paused = false, restarting = false;
            if (root.TryGetProperty("State", out var st))
            {
                if (st.TryGetProperty("Pid", out var p)) pid = p.GetInt32();
                if (st.TryGetProperty("Running", out var r)) running = r.GetBoolean();
                if (st.TryGetProperty("Paused", out var pz)) paused = pz.GetBoolean();
                if (st.TryGetProperty("Restarting", out var rs)) restarting = rs.GetBoolean();
            }

            string? workDir = null;
            string? image = null;
            if (root.TryGetProperty("Config", out var cfg))
            {
                if (cfg.TryGetProperty("WorkingDir", out var wd)) workDir = wd.GetString();
                if (cfg.TryGetProperty("Image", out var img)) image = img.GetString();
            }

            string? imageId = root.TryGetProperty("Image", out var imgId) ? imgId.GetString() : null;
            string? mergedDir = null;
            string? upperDir = null;

            if (root.TryGetProperty("GraphDriver", out var gd) && gd.TryGetProperty("Data", out var gdData))
            {
                if (gdData.TryGetProperty("MergedDir", out var md)) mergedDir = md.GetString();
                if (gdData.TryGetProperty("UpperDir", out var ud)) upperDir = ud.GetString();
            }

            return new DockerContainerInspect
            {
                Id = realId,
                Name = name,
                Pid = pid,
                Running = running,
                Paused = paused,
                Restarting = restarting,
                WorkingDir = workDir,
                Image = image,
                ImageId = imageId,
                MergedDir = mergedDir,
                UpperDir = upperDir
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "InspectContainerAsync failed for {ContainerId}", id);
            return null;
        }
    }

    public async Task<DockerContainerExecResult> ExecuteCommandAsync(string containerId, DockerContainerExecRequest request, CancellationToken cancellationToken = default)
    {
        var id = (containerId ?? "").Trim();
        var sw = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(request.Command))
        {
            return new DockerContainerExecResult
            {
                Success = false,
                ExitCode = -1,
                Error = "容器 ID 或执行命令不能为空",
                DurationMs = 0
            };
        }

        // 1. 尝试使用 nsenter 直接穿透容器（运行中且宿主特权环境下，执行速度极快，零 Daemon 序列化开销）
        try
        {
            var inspect = await InspectContainerAsync(id, cancellationToken);
            if (inspect != null && inspect.Running && inspect.Pid > 0 && OperatingSystem.IsLinux())
            {
                var nsenter = File.Exists("/usr/bin/nsenter") ? "/usr/bin/nsenter" : (File.Exists("/bin/nsenter") ? "/bin/nsenter" : null);
                if (nsenter != null && Directory.Exists($"/proc/{inspect.Pid}/ns"))
                {
                    // 自动探测 Shell
                    var root = $"/proc/{inspect.Pid}/root";
                    var shell = File.Exists($"{root}/bin/bash") ? "/bin/bash" :
                                (File.Exists($"{root}/bin/sh") ? "/bin/sh" :
                                (File.Exists($"{root}/bin/ash") ? "/bin/ash" : "sh"));

                    var targetDir = !string.IsNullOrWhiteSpace(request.WorkingDir) ? request.WorkingDir :
                                    (!string.IsNullOrWhiteSpace(inspect.WorkingDir) ? inspect.WorkingDir : "/");

                    var shellReq = new ShellRequest
                    {
                        CommandText = $"{nsenter} -t {inspect.Pid} -m -u -i -n -p -- env -u LC_ALL LANG=C.UTF-8 {shell} -c 'cd \"$1\" 2>/dev/null || cd /; shift; exec \"$@\"' sh \"{targetDir}\" {shell} -c {EscapeShellArgument(request.Command)}",
                        TimeoutSeconds = Math.Clamp(request.TimeoutSeconds, 1, 300)
                    };

                    var shellRes = await _shellExecutor.ExecuteAsync(shellReq, cancellationToken);
                    sw.Stop();

                    return new DockerContainerExecResult
                    {
                        Success = shellRes.ExitCode == 0,
                        ExitCode = shellRes.ExitCode ?? -1,
                        Output = shellRes.StandardOutput ?? "",
                        Error = shellRes.ErrorOutput ?? "",
                        DurationMs = sw.ElapsedMilliseconds
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "nsenter execution failed, falling back to Docker Socket API");
        }

        // 2. 降级使用 Docker Socket 原生 Exec API
        try
        {
            var createExecPayload = new DockerCreateExecRequestPayload
            {
                AttachStdout = true,
                AttachStderr = true,
                Tty = false,
                Cmd = ["/bin/sh", "-c", request.Command],
                WorkingDir = request.WorkingDir,
                User = request.User
            };
            var createJson = JsonSerializer.Serialize(createExecPayload, DockerExecJsonContext.Default.DockerCreateExecRequestPayload);
            using var createContent = new StringContent(createJson, System.Text.Encoding.UTF8, "application/json");
            using var createRes = await _httpClient.PostAsync($"/containers/{id}/exec", createContent, cancellationToken);
            if (!createRes.IsSuccessStatusCode)
            {
                sw.Stop();
                var err = await createRes.Content.ReadAsStringAsync(cancellationToken);
                return new DockerContainerExecResult
                {
                    Success = false,
                    ExitCode = (int)createRes.StatusCode,
                    Error = $"Docker 创建 Exec 实例失败: {err}",
                    DurationMs = sw.ElapsedMilliseconds
                };
            }

            var createResJson = await createRes.Content.ReadAsStringAsync(cancellationToken);
            using var createDoc = JsonDocument.Parse(createResJson);
            var execId = createDoc.RootElement.GetProperty("Id").GetString()!;

            var startExecPayload = new DockerStartExecRequestPayload { Detach = false, Tty = false };
            var startJson = JsonSerializer.Serialize(startExecPayload, DockerExecJsonContext.Default.DockerStartExecRequestPayload);
            using var startContent = new StringContent(startJson, System.Text.Encoding.UTF8, "application/json");
            using var startRes = await _httpClient.PostAsync($"/exec/{execId}/start", startContent, cancellationToken);

            var streamBytes = await startRes.Content.ReadAsByteArrayAsync(cancellationToken);
            var (stdout, stderr) = DemuxDockerStream(streamBytes);

            int exitCode = 0;
            try
            {
                using var inspectRes = await _httpClient.GetAsync($"/exec/{execId}/json", cancellationToken);
                if (inspectRes.IsSuccessStatusCode)
                {
                    var inspectJson = await inspectRes.Content.ReadAsStringAsync(cancellationToken);
                    using var inspectDoc = JsonDocument.Parse(inspectJson);
                    if (inspectDoc.RootElement.TryGetProperty("ExitCode", out var ec) && ec.ValueKind == JsonValueKind.Number)
                    {
                        exitCode = ec.GetInt32();
                    }
                }
            }
            catch { }

            sw.Stop();
            return new DockerContainerExecResult
            {
                Success = exitCode == 0,
                ExitCode = exitCode,
                Output = stdout,
                Error = stderr,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Docker exec failed for container {ContainerId}", id);
            return new DockerContainerExecResult
            {
                Success = false,
                ExitCode = -1,
                Error = "执行异常: " + ex.Message,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
    }

    public async Task<Stream?> GetArchiveAsync(string containerId, string path, CancellationToken cancellationToken = default)
    {
        var id = (containerId ?? "").Trim();
        var safePath = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();
        try
        {
            var url = $"/containers/{id}/archive?path={Uri.EscapeDataString(safePath)}";
            var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                return null;
            }
            return await response.Content.ReadAsStreamAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetArchiveAsync failed for {ContainerId}:{Path}", id, path);
            return null;
        }
    }

    public async Task<bool> PutArchiveAsync(string containerId, string path, Stream tarStream, CancellationToken cancellationToken = default)
    {
        var id = (containerId ?? "").Trim();
        var safePath = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();
        try
        {
            var url = $"/containers/{id}/archive?path={Uri.EscapeDataString(safePath)}";
            using var content = new StreamContent(tarStream);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-tar");
            using var response = await _httpClient.PutAsync(url, content, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PutArchiveAsync failed for {ContainerId}:{Path}", id, path);
            return false;
        }
    }

    private static (string Stdout, string Stderr) DemuxDockerStream(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return ("", "");

        var stdoutSb = new System.Text.StringBuilder();
        var stderrSb = new System.Text.StringBuilder();

        int pos = 0;
        while (pos + 8 <= bytes.Length)
        {
            byte type = bytes[pos];
            int size = (bytes[pos + 4] << 24) | (bytes[pos + 5] << 16) | (bytes[pos + 6] << 8) | bytes[pos + 7];
            pos += 8;

            if (size <= 0 || pos + size > bytes.Length)
            {
                stdoutSb.Append(System.Text.Encoding.UTF8.GetString(bytes, pos - 8, bytes.Length - (pos - 8)));
                break;
            }

            var text = System.Text.Encoding.UTF8.GetString(bytes, pos, size);
            if (type == 2) stderrSb.Append(text);
            else stdoutSb.Append(text);

            pos += size;
        }

        return (stdoutSb.ToString(), stderrSb.ToString());
    }

    private static string EscapeShellArgument(string arg)
    {
        return "'" + arg.Replace("'", "'\\''") + "'";
    }
    #endregion
}
