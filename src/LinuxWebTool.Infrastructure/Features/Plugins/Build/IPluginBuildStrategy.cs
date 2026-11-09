using LinuxWebTool.Contracts.Features.Plugins.Contracts;

namespace LinuxWebTool.Infrastructure.Features.Plugins.Build;

/// <summary>
/// 插件源码构建策略接口。
/// </summary>
public interface IPluginBuildStrategy
{
    bool CanHandle(PluginManifest manifest);
    Task<PluginBuildResult> BuildAsync(string pluginDir, PluginManifest manifest, CancellationToken ct = default);
}
