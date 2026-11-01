using LinuxWebTool.Contracts.Features.Plugins.Contracts;

namespace LinuxWebTool.Infrastructure.Features.Plugins.Adapters;

/// <summary>
/// 插件底层进程启动规格定义。
/// </summary>
public sealed record PluginStartSpec
{
    public required string FileName { get; init; }
    public required string Arguments { get; init; }
    public IReadOnlyList<string>? ArgumentList { get; init; }
    public required string WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// 插件多语言运行时统一适配器接口。
/// </summary>
public interface IRuntimeAdapter
{
    PluginRuntime RuntimeType { get; }
    PluginRuntime Runtime => RuntimeType;
    string PrimaryExecutable { get; }
    IReadOnlyList<string> CandidateExecutables { get; }

    bool CheckAvailable(out string? missingReason);
    bool CheckAvailable(out string? missingReason, out IReadOnlyList<string> missingRunners);
    string ResolveInstallGuide(string distroKey);

    PluginStartSpec BuildStartSpec(
        PluginManifest manifest,
        string pluginDir,
        int? port,
        IReadOnlyDictionary<string, string>? extraEnv = null);
}
