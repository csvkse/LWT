using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Contracts.Shared.Support;

namespace LinuxWebTool.Infrastructure.Features.Plugins;

public sealed partial class PluginManager
{
    public async ValueTask<IReadOnlyList<ServiceDescriptorDto>> DiscoverServicesAsync(CancellationToken ct = default)
    {
        var all = await GetAllAsync(null, ct);
        var manifests = all.Where(p => p.IsEnabled).Select(p => p.Manifest).ToList();
        return _broker.DiscoverServices(manifests, _stateMachines, _portManager);
    }

    public async ValueTask<PluginInvokeResponse> InvokeServiceAsync(PluginInvokeRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.PluginId) || !PluginSecurityGuard.IsValidPluginId(request.PluginId))
        {
            return new PluginInvokeResponse
            {
                Success = false,
                StatusCode = 400,
                Output = $"非法插件 ID: {request.PluginId}"
            };
        }

        var instance = await GetByIdAsync(request.PluginId, ct);
        if (instance == null)
        {
            var all = await GetAllAsync(null, ct);
            instance = all.FirstOrDefault(p => p.Manifest.Interface?.Services?.ContainsKey(request.PluginId) == true);
        }

        if (instance == null)
        {
            return new PluginInvokeResponse
            {
                Success = false,
                StatusCode = 404,
                Output = $"未找到插件: {request.PluginId}"
            };
        }

        var targetPluginId = instance.Id;
        var pluginDir = Path.Combine(_pluginsRoot, targetPluginId);
        _stateMachines.TryGetValue(targetPluginId, out var sm);

        return await _broker.InvokeServiceAsync(
            request,
            instance.Manifest,
            pluginDir,
            sm,
            _portManager,
            (req, token) => ExecuteActionAsync(req, token),
            ct);
    }

    public async ValueTask<PluginBuildResult> BuildPluginAsync(string id, CancellationToken ct = default)
    {
        if (!PluginSecurityGuard.IsValidPluginId(id))
        {
            return new PluginBuildResult
            {
                Success = false,
                ExitCode = 1,
                ErrorMessage = $"非法插件 ID: {id}"
            };
        }

        var instance = await GetByIdAsync(id, ct);
        if (instance == null)
        {
            return new PluginBuildResult
            {
                Success = false,
                ExitCode = 1,
                ErrorMessage = $"插件不存在或清单无效: {id}"
            };
        }

        var pluginDir = Path.Combine(_pluginsRoot, id);
        var result = await _buildManager.BuildAsync(pluginDir, instance.Manifest, ct);
        if (result.Success)
        {
            NotifyPluginStateOrManifestChanged();
        }

        return result;
    }
}
