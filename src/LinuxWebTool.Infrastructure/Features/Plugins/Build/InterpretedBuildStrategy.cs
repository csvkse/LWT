using LinuxWebTool.Contracts.Features.Plugins.Contracts;

namespace LinuxWebTool.Infrastructure.Features.Plugins.Build;

/// <summary>
/// 解释型语言与预编译二进制插件的免构建策略 (No-Op Strategy)。
/// </summary>
public sealed class InterpretedBuildStrategy : IPluginBuildStrategy
{
    public bool CanHandle(PluginManifest manifest)
    {
        var lc = manifest.GetEffectiveLifecycle();
        return lc is PluginLifecycle.Interpreted or PluginLifecycle.PrebuiltBinary;
    }

    public Task<PluginBuildResult> BuildAsync(string pluginDir, PluginManifest manifest, CancellationToken ct = default)
    {
        var lc = manifest.GetEffectiveLifecycle();
        return Task.FromResult(new PluginBuildResult
        {
            Success = true,
            ExitCode = 0,
            Output = $"[Build] 插件生命周期规格为 {lc}，具备即开即用特性，无需中间编译阶段。",
            ElapsedMs = 0
        });
    }
}
