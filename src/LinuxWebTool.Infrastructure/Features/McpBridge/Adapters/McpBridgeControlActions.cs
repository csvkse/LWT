using System.Security;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.McpBridge.Contracts;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;

namespace LinuxWebTool.Infrastructure.Features.McpBridge.Adapters;

/// <summary>
/// 将 McpBridge 类型化请求映射为固定的 Resident Control action：
/// - 统一执行 loopback 回环管理端点调用；
/// - 禁止调用者提供任意目标地址或任意 action 字符串；
/// - 严格校验控制面返回数据结构并映射错误。
/// </summary>
public sealed class McpBridgeControlActions : IMcpBridgeControlActions
{
    private readonly IResidentControlClient _client;

    public McpBridgeControlActions(IResidentControlClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async ValueTask<McpBridgeStatusDto> GetStatusAsync(ResidentControlSpec spec, string runId, CancellationToken ct)
    {
        var result = await _client.InvokeAsync(spec, runId, "status.get", CreateEmptyElement(), ct).ConfigureAwait(false);
        if (result.Status != "succeeded" || result.Data == null)
        {
            return new McpBridgeStatusDto
            {
                WorkerRunId = runId,
                ProcessAlive = true,
                AdminReady = false,
                TunnelState = "error",
                LastError = result.Error ?? "无法从 Bridge 进程获取管理状态。"
            };
        }

        try
        {
            return JsonSerializer.Deserialize(result.Data.Value.GetRawText(), McpBridgeJsonContext.Default.McpBridgeStatusDto)
                   ?? new McpBridgeStatusDto { WorkerRunId = runId, ProcessAlive = true, TunnelState = "error" };
        }
        catch (Exception ex)
        {
            return new McpBridgeStatusDto
            {
                WorkerRunId = runId,
                ProcessAlive = true,
                TunnelState = "error",
                LastError = $"反序列化状态数据失败: {ex.Message}"
            };
        }
    }

    public async ValueTask<McpBridgeMutationResult> SaveTunnelAsync(ResidentControlSpec spec, string runId, SaveTunnelRequest request, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToElement(request, McpBridgeJsonContext.Default.SaveTunnelRequest);
        var result = await _client.InvokeAsync(spec, runId, "tunnel.save", payload, ct).ConfigureAwait(false);
        return MapMutationResult(result);
    }

    public async ValueTask<McpBridgeOperationResult> StartTunnelAsync(ResidentControlSpec spec, string runId, CancellationToken ct)
    {
        var result = await _client.InvokeAsync(spec, runId, "tunnel.start", CreateEmptyElement(), ct).ConfigureAwait(false);
        return MapOperationResult(result);
    }

    public async ValueTask<McpBridgeOperationResult> StopTunnelAsync(ResidentControlSpec spec, string runId, CancellationToken ct)
    {
        var result = await _client.InvokeAsync(spec, runId, "tunnel.stop", CreateEmptyElement(), ct).ConfigureAwait(false);
        return MapOperationResult(result);
    }

    public async ValueTask<McpBridgeServersResult> GetServersAsync(ResidentControlSpec spec, string runId, CancellationToken ct)
    {
        var result = await _client.InvokeAsync(spec, runId, "servers.list", CreateEmptyElement(), ct).ConfigureAwait(false);
        if (result.Status != "succeeded" || result.Data == null)
        {
            return new McpBridgeServersResult();
        }

        try
        {
            return JsonSerializer.Deserialize(result.Data.Value.GetRawText(), McpBridgeJsonContext.Default.McpBridgeServersResult)
                   ?? new McpBridgeServersResult();
        }
        catch
        {
            return new McpBridgeServersResult();
        }
    }

    public async ValueTask<McpBridgeMutationResult> SaveServerAsync(ResidentControlSpec spec, string runId, string id, SaveServerRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new SecurityException("服务 ID 不能为空。");

        var payload = JsonSerializer.SerializeToElement(new
        {
            id,
            name = request.Name,
            transport = request.Transport,
            command = request.Command,
            arguments = request.Arguments,
            workingDirectory = request.WorkingDirectory,
            url = request.Url,
            secretAction = request.SecretAction,
            secretValue = request.SecretValue,
            enabled = request.Enabled
        }, McpBridgeJsonContext.Default.SaveServerRequest);

        var result = await _client.InvokeAsync(spec, runId, "servers.save", payload, ct).ConfigureAwait(false);
        return MapMutationResult(result);
    }

    public async ValueTask<McpBridgeMutationResult> DeleteServerAsync(ResidentControlSpec spec, string runId, string id, ulong expectedRevision, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new SecurityException("服务 ID 不能为空。");

        var payload = CreateIdAndRevisionElement(id, expectedRevision);
        var result = await _client.InvokeAsync(spec, runId, "servers.delete", payload, ct).ConfigureAwait(false);
        return MapMutationResult(result);
    }

    public async ValueTask<McpBridgeProbeResult> ProbeAsync(ResidentControlSpec spec, string runId, string id, CancellationToken ct)
    {
        var payload = CreateIdElement(id);
        var result = await _client.InvokeAsync(spec, runId, "servers.probe", payload, ct).ConfigureAwait(false);

        if (result.Status != "succeeded" || result.Data == null)
        {
            return new McpBridgeProbeResult
            {
                Success = false,
                Status = "error",
                Message = result.Error ?? "探针探测失败"
            };
        }

        try
        {
            return JsonSerializer.Deserialize(result.Data.Value.GetRawText(), McpBridgeJsonContext.Default.McpBridgeProbeResult)
                   ?? new McpBridgeProbeResult { Success = false, Status = "error" };
        }
        catch (Exception ex)
        {
            return new McpBridgeProbeResult { Success = false, Status = "error", Message = ex.Message };
        }
    }

    public async ValueTask<McpBridgeDiscoveryResult> DiscoverAsync(ResidentControlSpec spec, string runId, string id, CancellationToken ct)
    {
        var payload = CreateIdElement(id);
        var result = await _client.InvokeAsync(spec, runId, "servers.discover", payload, ct).ConfigureAwait(false);

        if (result.Status != "succeeded" || result.Data == null)
        {
            return new McpBridgeDiscoveryResult
            {
                Success = false,
                Message = result.Error ?? "目录发现任务提交失败"
            };
        }

        try
        {
            return JsonSerializer.Deserialize(result.Data.Value.GetRawText(), McpBridgeJsonContext.Default.McpBridgeDiscoveryResult)
                   ?? new McpBridgeDiscoveryResult { Success = false };
        }
        catch (Exception ex)
        {
            return new McpBridgeDiscoveryResult { Success = false, Message = ex.Message };
        }
    }

    public async ValueTask<McpBridgeToolsPage> GetToolsAsync(ResidentControlSpec spec, string runId, string id, string? cursor, CancellationToken ct)
    {
        var payload = CreateIdAndCursorElement(id, cursor);
        var result = await _client.InvokeAsync(spec, runId, "tools.list", payload, ct).ConfigureAwait(false);

        if (result.Status != "succeeded" || result.Data == null)
        {
            return new McpBridgeToolsPage();
        }

        try
        {
            return JsonSerializer.Deserialize(result.Data.Value.GetRawText(), McpBridgeJsonContext.Default.McpBridgeToolsPage)
                   ?? new McpBridgeToolsPage();
        }
        catch
        {
            return new McpBridgeToolsPage();
        }
    }

    public async ValueTask<McpBridgeMutationResult> SaveSelectionAsync(ResidentControlSpec spec, string runId, string id, SaveSelectionRequest request, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            id,
            expectedRevision = request.ExpectedRevision,
            selectedExportedNames = request.SelectedExportedNames
        }, McpBridgeJsonContext.Default.SaveSelectionRequest);

        var result = await _client.InvokeAsync(spec, runId, "tools.select", payload, ct).ConfigureAwait(false);
        return MapMutationResult(result);
    }

    public async ValueTask<McpBridgeApplyResult> ApplyAsync(ResidentControlSpec spec, string runId, ApplyConfigRequest request, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToElement(request, McpBridgeJsonContext.Default.ApplyConfigRequest);
        var result = await _client.InvokeAsync(spec, runId, "config.apply", payload, ct).ConfigureAwait(false);

        if (result.Status != "succeeded" || result.Data == null)
        {
            return new McpBridgeApplyResult
            {
                Success = false,
                Message = result.Error ?? "应用配置失败。"
            };
        }

        try
        {
            return JsonSerializer.Deserialize(result.Data.Value.GetRawText(), McpBridgeJsonContext.Default.McpBridgeApplyResult)
                   ?? new McpBridgeApplyResult { Success = false };
        }
        catch (Exception ex)
        {
            return new McpBridgeApplyResult { Success = false, Message = ex.Message };
        }
    }

    public async ValueTask<McpBridgeAuditPage> GetAuditAsync(ResidentControlSpec spec, string runId, string? cursor, int limit, CancellationToken ct)
    {
        var payload = CreateCursorAndLimitElement(cursor, limit);
        var result = await _client.InvokeAsync(spec, runId, "audit.list", payload, ct).ConfigureAwait(false);

        if (result.Status != "succeeded" || result.Data == null)
        {
            return new McpBridgeAuditPage();
        }

        try
        {
            return JsonSerializer.Deserialize(result.Data.Value.GetRawText(), McpBridgeJsonContext.Default.McpBridgeAuditPage)
                   ?? new McpBridgeAuditPage();
        }
        catch
        {
            return new McpBridgeAuditPage();
        }
    }

    private static McpBridgeMutationResult MapMutationResult(PluginActionResult result)
    {
        if (result.Status != "succeeded" || result.Data == null)
        {
            return new McpBridgeMutationResult
            {
                Success = false,
                Message = result.Error ?? "操作失败"
            };
        }

        try
        {
            return JsonSerializer.Deserialize(result.Data.Value.GetRawText(), McpBridgeJsonContext.Default.McpBridgeMutationResult)
                   ?? new McpBridgeMutationResult { Success = false };
        }
        catch (Exception ex)
        {
            return new McpBridgeMutationResult { Success = false, Message = ex.Message };
        }
    }

    private static McpBridgeOperationResult MapOperationResult(PluginActionResult result)
    {
        if (result.Status != "succeeded")
        {
            return new McpBridgeOperationResult
            {
                Success = false,
                Message = result.Error ?? "操作失败"
            };
        }

        return new McpBridgeOperationResult
        {
            Success = true,
            Message = result.Message ?? "操作已执行"
        };
    }

    private static JsonElement CreateIdAndRevisionElement(string id, ulong expectedRevision)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("id", id);
            writer.WriteNumber("expectedRevision", expectedRevision);
            writer.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }

    private static JsonElement CreateIdElement(string id)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("id", id);
            writer.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }

    private static JsonElement CreateIdAndCursorElement(string id, string? cursor)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("id", id);
            if (cursor != null) writer.WriteString("cursor", cursor);
            writer.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }

    private static JsonElement CreateCursorAndLimitElement(string? cursor, int? limit)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (cursor != null) writer.WriteString("cursor", cursor);
            if (limit.HasValue) writer.WriteNumber("limit", limit.Value);
            writer.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }

    private static JsonElement CreateEmptyElement()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }
}
