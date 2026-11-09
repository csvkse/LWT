using LinuxWebTool.Contracts.Features.Plugins.Contracts;

namespace LinuxWebTool.Infrastructure.Features.Plugins.Build;

/// <summary>
/// 插件构建管道统一管理器 (Plugin Build Pipeline Manager)
/// </summary>
public sealed class PluginBuildManager
{
    private readonly IReadOnlyList<IPluginBuildStrategy> _strategies;

    public PluginBuildManager()
    {
        _strategies = new IPluginBuildStrategy[]
        {
            new SourceCompiledBuildStrategy(),
            new InterpretedBuildStrategy()
        };
    }

    public async Task<PluginBuildResult> BuildAsync(string pluginDir, PluginManifest manifest, CancellationToken ct = default)
    {
        var strategy = _strategies.FirstOrDefault(s => s.CanHandle(manifest));
        if (strategy == null)
        {
            return new PluginBuildResult
            {
                Success = false,
                ExitCode = 1,
                ErrorMessage = $"未找到适用于生命周期规格 {manifest.GetEffectiveLifecycle()} 的构建策略"
            };
        }

        return await strategy.BuildAsync(pluginDir, manifest, ct);
    }
}
