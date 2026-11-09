using System.Text.Json;
using LinuxWebTool.Contracts.Features.Services.Contracts;
using LinuxWebTool.Contracts.Features.Shell.Contracts;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Services.Adapters.Docker;

public sealed partial class DockerEngineAdapter
{
    public async Task<DockerDiskUsageSummary?> GetDiskUsageAsync(CancellationToken cancellationToken = default)
    {
        // 1. 尝试 Unix Socket / Named Pipe 获取 /system/df
        try
        {
            using var response = await _httpClient.GetAsync("/system/df", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                return ParseDiskUsageJson(json);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Docker /system/df socket request failed, falling back to CLI");
        }

        // 2. 降级使用 docker system df --format "{{json .}}"
        return await GetDiskUsageCliAsync(cancellationToken);
    }

    public async Task<DockerPruneResult> PruneAsync(DockerPruneRequest request, CancellationToken cancellationToken = default)
    {
        var type = (request.PruneType ?? "builder").Trim().ToLowerInvariant();

        // 1. 优先通过 Docker Unix Socket 原生 API 执行 Prune（无依赖、毫秒级响应、带精确字节数）
        var socketResult = await PruneSocketAsync(type, request, cancellationToken);
        if (socketResult != null)
        {
            return socketResult;
        }

        // 2. 降级使用 docker CLI
        string cmd;
        int timeout;
        string defaultMsg;

        switch (type)
        {
            case "builder":
            case "build":
            case "buildcache":
                cmd = "docker builder prune -a -f";
                timeout = 120;
                defaultMsg = "BuildKit 构建缓存清理完成";
                break;
            case "images":
            case "image":
                cmd = request.All ? "docker image prune -a -f" : "docker image prune -f";
                timeout = 90;
                defaultMsg = "Docker 镜像清理完成";
                break;
            case "containers":
            case "container":
                cmd = "docker container prune -f";
                timeout = 45;
                defaultMsg = "已停止容器清理完成";
                break;
            case "volumes":
            case "volume":
                cmd = "docker volume prune -f";
                timeout = 45;
                defaultMsg = "无主数据卷清理完成";
                break;
            case "system":
            default:
                cmd = request.Volumes ? "docker system prune -a --volumes -f" : "docker system prune -f";
                timeout = 180;
                defaultMsg = "Docker 全系统清理完成";
                break;
        }

        try
        {
            var res = await _shellExecutor.ExecuteAsync(new ShellRequest
            {
                CommandText = cmd,
                TimeoutSeconds = timeout
            }, cancellationToken);

            var stdout = res.StandardOutput ?? "";
            var stderr = res.ErrorOutput ?? "";
            var combined = (stdout + "\n" + stderr).Trim();

            if (res.ExitCode != 0)
            {
                return new DockerPruneResult
                {
                    Success = false,
                    Message = $"清理失败 (退出码 {res.ExitCode})",
                    Output = combined
                };
            }

            var reclaimedHuman = "";
            var match = System.Text.RegularExpressions.Regex.Match(combined, @"Total reclaimed space:\s*([0-9.]+\s*[A-Za-z]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                reclaimedHuman = match.Groups[1].Value.Trim();
            }

            return new DockerPruneResult
            {
                Success = true,
                Message = string.IsNullOrEmpty(reclaimedHuman) ? defaultMsg : $"{defaultMsg}，释放空间: {reclaimedHuman}",
                Output = combined,
                ReclaimedHuman = reclaimedHuman,
                ReclaimedBytes = ParseHumanSizeToBytes(reclaimedHuman)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Docker prune failed for type {Type}", type);
            return new DockerPruneResult
            {
                Success = false,
                Message = "执行清理异常: " + ex.Message
            };
        }
    }

    private async Task<DockerPruneResult?> PruneSocketAsync(string type, DockerPruneRequest request, CancellationToken cancellationToken)
    {
        try
        {
            switch (type)
            {
                case "builder":
                case "build":
                case "buildcache":
                {
                    using var res = await _httpClient.PostAsync("/build/prune?all=true", null, cancellationToken);
                    if (!res.IsSuccessStatusCode) return null;
                    var json = await res.Content.ReadAsStringAsync(cancellationToken);
                    using var doc = JsonDocument.Parse(json);
                    var bytes = doc.RootElement.TryGetProperty("SpaceReclaimed", out var sr) && sr.TryGetInt64(out var b) ? b : 0;
                    var count = 0;
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("Deleted BuildKit Cache:");
                    if (doc.RootElement.TryGetProperty("CachesDeleted", out var cd) && cd.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in cd.EnumerateArray())
                        {
                            count++;
                            sb.AppendLine($"- {item.GetString()}");
                        }
                    }
                    var human = FormatBytes(bytes);
                    sb.AppendLine($"\nTotal reclaimed space: {human}");
                    return new DockerPruneResult
                    {
                        Success = true,
                        Message = $"BuildKit 构建缓存清理完成 (清除 {count} 项)，释放空间: {human}",
                        Output = sb.ToString(),
                        ReclaimedHuman = human,
                        ReclaimedBytes = bytes
                    };
                }

                case "images":
                case "image":
                {
                    var filter = request.All ? "?filters=%7B%22dangling%22%3A%5B%22false%22%5D%7D" : "";
                    using var res = await _httpClient.PostAsync($"/images/prune{filter}", null, cancellationToken);
                    if (!res.IsSuccessStatusCode) return null;
                    var json = await res.Content.ReadAsStringAsync(cancellationToken);
                    using var doc = JsonDocument.Parse(json);
                    var bytes = doc.RootElement.TryGetProperty("SpaceReclaimed", out var sr) && sr.TryGetInt64(out var b) ? b : 0;
                    var count = 0;
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("Deleted Images:");
                    if (doc.RootElement.TryGetProperty("ImagesDeleted", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in arr.EnumerateArray())
                        {
                            count++;
                            if (item.TryGetProperty("Deleted", out var del))
                                sb.AppendLine($"deleted: {del.GetString()}");
                            else if (item.TryGetProperty("Untagged", out var untag))
                                sb.AppendLine($"untagged: {untag.GetString()}");
                        }
                    }
                    var human = FormatBytes(bytes);
                    sb.AppendLine($"\nTotal reclaimed space: {human}");
                    return new DockerPruneResult
                    {
                        Success = true,
                        Message = $"Docker 镜像清理完成 (清除 {count} 项)，释放空间: {human}",
                        Output = sb.ToString(),
                        ReclaimedHuman = human,
                        ReclaimedBytes = bytes
                    };
                }

                case "containers":
                case "container":
                {
                    using var res = await _httpClient.PostAsync("/containers/prune", null, cancellationToken);
                    if (!res.IsSuccessStatusCode) return null;
                    var json = await res.Content.ReadAsStringAsync(cancellationToken);
                    using var doc = JsonDocument.Parse(json);
                    var bytes = doc.RootElement.TryGetProperty("SpaceReclaimed", out var sr) && sr.TryGetInt64(out var b) ? b : 0;
                    var count = 0;
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("Deleted Containers:");
                    if (doc.RootElement.TryGetProperty("ContainersDeleted", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in arr.EnumerateArray())
                        {
                            count++;
                            sb.AppendLine($"- {item.GetString()}");
                        }
                    }
                    var human = FormatBytes(bytes);
                    sb.AppendLine($"\nTotal reclaimed space: {human}");
                    return new DockerPruneResult
                    {
                        Success = true,
                        Message = $"已停止容器清理完成 (清除 {count} 个)，释放空间: {human}",
                        Output = sb.ToString(),
                        ReclaimedHuman = human,
                        ReclaimedBytes = bytes
                    };
                }

                case "volumes":
                case "volume":
                {
                    using var res = await _httpClient.PostAsync("/volumes/prune", null, cancellationToken);
                    if (!res.IsSuccessStatusCode) return null;
                    var json = await res.Content.ReadAsStringAsync(cancellationToken);
                    using var doc = JsonDocument.Parse(json);
                    var bytes = doc.RootElement.TryGetProperty("SpaceReclaimed", out var sr) && sr.TryGetInt64(out var b) ? b : 0;
                    var count = 0;
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("Deleted Volumes:");
                    if (doc.RootElement.TryGetProperty("VolumesDeleted", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in arr.EnumerateArray())
                        {
                            count++;
                            sb.AppendLine($"- {item.GetString()}");
                        }
                    }
                    var human = FormatBytes(bytes);
                    sb.AppendLine($"\nTotal reclaimed space: {human}");
                    return new DockerPruneResult
                    {
                        Success = true,
                        Message = $"无主数据卷清理完成 (清除 {count} 个)，释放空间: {human}",
                        Output = sb.ToString(),
                        ReclaimedHuman = human,
                        ReclaimedBytes = bytes
                    };
                }

                case "system":
                default:
                {
                    long totalBytes = 0;
                    var sb = new System.Text.StringBuilder();

                    // 1. Containers
                    var cRes = await PruneSocketAsync("containers", request, cancellationToken);
                    if (cRes != null)
                    {
                        totalBytes += cRes.ReclaimedBytes;
                        sb.AppendLine(cRes.Output.Trim());
                        sb.AppendLine();
                    }

                    // 2. Networks
                    try
                    {
                        using var nRes = await _httpClient.PostAsync("/networks/prune", null, cancellationToken);
                        if (nRes.IsSuccessStatusCode)
                        {
                            var nJson = await nRes.Content.ReadAsStringAsync(cancellationToken);
                            using var nDoc = JsonDocument.Parse(nJson);
                            if (nDoc.RootElement.TryGetProperty("NetworksDeleted", out var nArr) && nArr.ValueKind == JsonValueKind.Array)
                            {
                                sb.AppendLine("Deleted Networks:");
                                foreach (var net in nArr.EnumerateArray()) sb.AppendLine($"- {net.GetString()}");
                                sb.AppendLine();
                            }
                        }
                    }
                    catch { }

                    // 3. Images
                    var iRes = await PruneSocketAsync("images", request, cancellationToken);
                    if (iRes != null)
                    {
                        totalBytes += iRes.ReclaimedBytes;
                        sb.AppendLine(iRes.Output.Trim());
                        sb.AppendLine();
                    }

                    // 4. Volumes (if requested)
                    if (request.Volumes)
                    {
                        var vRes = await PruneSocketAsync("volumes", request, cancellationToken);
                        if (vRes != null)
                        {
                            totalBytes += vRes.ReclaimedBytes;
                            sb.AppendLine(vRes.Output.Trim());
                            sb.AppendLine();
                        }
                    }

                    // 5. Build cache
                    var bRes = await PruneSocketAsync("builder", request, cancellationToken);
                    if (bRes != null)
                    {
                        totalBytes += bRes.ReclaimedBytes;
                        sb.AppendLine(bRes.Output.Trim());
                        sb.AppendLine();
                    }

                    var totalHuman = FormatBytes(totalBytes);
                    sb.AppendLine($"=========================================");
                    sb.AppendLine($"Total System Reclaimed Space: {totalHuman}");
                    return new DockerPruneResult
                    {
                        Success = true,
                        Message = $"Docker 全系统清理完成，总释放空间: {totalHuman}",
                        Output = sb.ToString(),
                        ReclaimedHuman = totalHuman,
                        ReclaimedBytes = totalBytes
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Docker socket prune failed for type {Type}, falling back to CLI", type);
            return null;
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{(bytes / 1024.0):F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{(bytes / (1024.0 * 1024.0)):F2} MB";
        return $"{(bytes / (1024.0 * 1024.0 * 1024.0)):F2} GB";
    }

    private static long ParseHumanSizeToBytes(string human)
    {
        if (string.IsNullOrWhiteSpace(human)) return 0;
        var clean = human.Trim().ToUpperInvariant();
        var numStr = System.Text.RegularExpressions.Regex.Match(clean, @"^[0-9.]+").Value;
        if (!double.TryParse(numStr, System.Globalization.CultureInfo.InvariantCulture, out var num)) return 0;

        if (clean.EndsWith("TB") || clean.EndsWith("TIB")) return (long)(num * 1024 * 1024 * 1024 * 1024);
        if (clean.EndsWith("GB") || clean.EndsWith("GIB")) return (long)(num * 1024 * 1024 * 1024);
        if (clean.EndsWith("MB") || clean.EndsWith("MIB")) return (long)(num * 1024 * 1024);
        if (clean.EndsWith("KB") || clean.EndsWith("KIB")) return (long)(num * 1024);
        if (clean.EndsWith("B")) return (long)num;
        return (long)num;
    }

    private async Task<DockerDiskUsageSummary?> GetDiskUsageCliAsync(CancellationToken cancellationToken)
    {
        try
        {
            var res = await _shellExecutor.ExecuteAsync(new ShellRequest
            {
                CommandText = "docker system df --format \"{{json .}}\"",
                TimeoutSeconds = 10
            }, cancellationToken);

            if (res.ExitCode != 0 || string.IsNullOrWhiteSpace(res.StandardOutput))
            {
                return null;
            }

            var summary = new DockerDiskUsageSummary();
            var lines = res.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var line in lines)
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var type = root.TryGetProperty("Type", out var pt) ? pt.GetString() ?? "" : "";
                    var totalCountStr = root.TryGetProperty("TotalCount", out var ptc) ? ptc.GetString() ?? "0" : "0";
                    var activeStr = root.TryGetProperty("Active", out var pa) ? pa.GetString() ?? "0" : "0";
                    var sizeStr = root.TryGetProperty("Size", out var ps) ? ps.GetString() ?? "0B" : "0B";
                    var reclaimableStr = root.TryGetProperty("Reclaimable", out var pr) ? pr.GetString() ?? "0B" : "0B";

                    _ = int.TryParse(totalCountStr, out var totalCount);
                    _ = int.TryParse(activeStr, out var active);
                    var totalBytes = ParseHumanSizeToBytes(sizeStr);
                    var reclaimableBytes = ParseHumanSizeToBytes(reclaimableStr);

                    var cat = new DockerDiskUsageCategory
                    {
                        TotalCount = totalCount,
                        ActiveCount = active,
                        TotalBytes = totalBytes,
                        ReclaimableBytes = reclaimableBytes
                    };

                    if (type.Contains("Image", StringComparison.OrdinalIgnoreCase))
                    {
                        summary = summary with { Images = cat };
                    }
                    else if (type.Contains("Container", StringComparison.OrdinalIgnoreCase))
                    {
                        summary = summary with { Containers = cat };
                    }
                    else if (type.Contains("Volume", StringComparison.OrdinalIgnoreCase))
                    {
                        summary = summary with { LocalVolumes = cat };
                    }
                    else if (type.Contains("Build", StringComparison.OrdinalIgnoreCase))
                    {
                        summary = summary with { BuildCache = cat };
                    }
                }
                catch { }
            }
            return summary;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetDiskUsageCliAsync failed");
            return null;
        }
    }

    private static DockerDiskUsageSummary ParseDiskUsageJson(string json)
    {
        var summary = new DockerDiskUsageSummary();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Images
        if (root.TryGetProperty("Images", out var imagesEl) && imagesEl.ValueKind == JsonValueKind.Array)
        {
            int total = 0, active = 0;
            long totalBytes = 0, reclaimableBytes = 0;
            foreach (var img in imagesEl.EnumerateArray())
            {
                total++;
                var size = img.TryGetProperty("Size", out var sz) ? sz.GetInt64() : 0;
                var containers = img.TryGetProperty("Containers", out var c) ? c.GetInt32() : 0;
                totalBytes += size;
                if (containers > 0) active++;
                else reclaimableBytes += size;
            }
            summary = summary with { Images = new DockerDiskUsageCategory { TotalCount = total, ActiveCount = active, TotalBytes = totalBytes, ReclaimableBytes = reclaimableBytes } };
        }

        // Containers
        if (root.TryGetProperty("Containers", out var contEl) && contEl.ValueKind == JsonValueKind.Array)
        {
            int total = 0, active = 0;
            long totalBytes = 0, reclaimableBytes = 0;
            foreach (var ct in contEl.EnumerateArray())
            {
                total++;
                var sizeRw = ct.TryGetProperty("SizeRw", out var sz) ? sz.GetInt64() : 0;
                var state = ct.TryGetProperty("State", out var st) ? st.GetString() : "";
                totalBytes += sizeRw;
                if (string.Equals(state, "running", StringComparison.OrdinalIgnoreCase)) active++;
                else reclaimableBytes += sizeRw;
            }
            summary = summary with { Containers = new DockerDiskUsageCategory { TotalCount = total, ActiveCount = active, TotalBytes = totalBytes, ReclaimableBytes = reclaimableBytes } };
        }

        // Volumes
        if (root.TryGetProperty("Volumes", out var volEl) && volEl.ValueKind == JsonValueKind.Array)
        {
            int total = 0, active = 0;
            long totalBytes = 0, reclaimableBytes = 0;
            foreach (var v in volEl.EnumerateArray())
            {
                total++;
                long size = 0;
                long refCount = 0;
                if (v.TryGetProperty("UsageData", out var ud) && ud.ValueKind == JsonValueKind.Object)
                {
                    size = ud.TryGetProperty("Size", out var sz) ? sz.GetInt64() : 0;
                    refCount = ud.TryGetProperty("RefCount", out var rc) ? rc.GetInt64() : 0;
                }
                totalBytes += size;
                if (refCount > 0) active++;
                else reclaimableBytes += size;
            }
            summary = summary with { LocalVolumes = new DockerDiskUsageCategory { TotalCount = total, ActiveCount = active, TotalBytes = totalBytes, ReclaimableBytes = reclaimableBytes } };
        }

        // BuildCache
        if (root.TryGetProperty("BuildCache", out var bcEl) && bcEl.ValueKind == JsonValueKind.Array)
        {
            int total = 0, active = 0;
            long totalBytes = 0, reclaimableBytes = 0;
            foreach (var b in bcEl.EnumerateArray())
            {
                total++;
                var size = b.TryGetProperty("Size", out var sz) ? sz.GetInt64() : 0;
                var inUse = b.TryGetProperty("InUse", out var iu) && iu.GetBoolean();
                totalBytes += size;
                if (inUse) active++;
                else reclaimableBytes += size;
            }
            summary = summary with { BuildCache = new DockerDiskUsageCategory { TotalCount = total, ActiveCount = active, TotalBytes = totalBytes, ReclaimableBytes = reclaimableBytes } };
        }

        return summary;
    }
}
