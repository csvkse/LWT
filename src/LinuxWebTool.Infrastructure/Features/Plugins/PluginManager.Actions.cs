using System.Text.Json;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Contracts.Shared.Support;
using LinuxWebTool.Infrastructure.Features.Plugins.Adapters;

namespace LinuxWebTool.Infrastructure.Features.Plugins;

public sealed partial class PluginManager
{
    public async ValueTask<PluginActionResult> ExecuteActionAsync(PluginActionRequest request, CancellationToken ct = default)
    {
        if (!PluginSecurityGuard.IsValidPluginId(request.PluginId))
        {
            return new PluginActionResult { Status = "failed", Message = $"非法插件 ID: {request.PluginId}" };
        }

        var dir = Path.Combine(_pluginsRoot, request.PluginId);
        if (!Directory.Exists(dir))
        {
            return new PluginActionResult { Status = "failed", Message = $"未找到插件: {request.PluginId}" };
        }

        var effectiveDir = Supervisor.PluginRevisionManager.ResolveEffectiveDirectory(dir);

        if (File.Exists(Path.Combine(dir, ".disabled")))
        {
            return new PluginActionResult { Status = "failed", Message = $"插件 '{request.PluginId}' 已被显式禁用，禁止执行任何操作。" };
        }

        var manifestPath = Path.Combine(effectiveDir, "plugin.json");
        if (!File.Exists(manifestPath)) manifestPath = Path.Combine(dir, "plugin.json");
        if (!File.Exists(manifestPath))
        {
            return new PluginActionResult { Status = "failed", Message = $"未找到插件清单 plugin.json" };
        }

        var manifestJson = await File.ReadAllTextAsync(manifestPath, ct);
        var manifest = JsonSerializer.Deserialize(manifestJson, PluginJsonContext.Default.PluginManifest);
        if (manifest == null)
        {
            return new PluginActionResult { Status = "failed", Message = "解析插件清单失败" };
        }

        var effectiveAction = request.Action;
        if (string.IsNullOrWhiteSpace(effectiveAction))
        {
            effectiveAction = manifest.CustomActions?.FirstOrDefault()?.Action ?? "default";
        }

        if (!PluginSecurityGuard.IsValidActionName(effectiveAction) && effectiveAction != "default")
        {
            return new PluginActionResult { Status = "failed", Message = $"非法动作名称: {request.Action}" };
        }

        var effectiveRequest = request with { Action = effectiveAction };
        var strategy = GetStrategy(manifest.Profile);
        var pipeline = Supervisor.PluginResiliencePipeline.GetOrCreate(manifest.Id);

        try
        {
            return await pipeline.ExecuteAsync(
                async token => await strategy.ExecuteActionAsync(manifest, effectiveDir, effectiveRequest, _adapters, token),
                ct).ConfigureAwait(false);
        }
        catch (Polly.CircuitBreaker.BrokenCircuitException)
        {
            return new PluginActionResult
            {
                Status = "failed",
                ErrorCode = "CIRCUIT_BROKEN",
                Message = $"插件 '{manifest.Id}' 当前处于熔断保护状态（连续故障率过高，暂时拒绝执行以保护系统）。"
            };
        }
        catch (Polly.Timeout.TimeoutRejectedException)
        {
            return new PluginActionResult
            {
                Status = "timedOut",
                ErrorCode = "TIMEOUT",
                Message = $"插件动作 '{effectiveAction}' 执行超时。"
            };
        }
    }

    public ValueTask<IReadOnlyList<string>> GetLogsAsync(string id, int limit = 200, CancellationToken ct = default)
    {
        if (_logBuffers.TryGetValue(id, out var ringBuffer))
        {
            return ValueTask.FromResult(ringBuffer.GetRecentLines(limit));
        }

        return ValueTask.FromResult<IReadOnlyList<string>>([]);
    }
}
