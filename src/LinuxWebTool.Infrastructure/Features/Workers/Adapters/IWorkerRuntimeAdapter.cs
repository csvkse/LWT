using System.Diagnostics;
using LinuxWebTool.Contracts.Features.Workers.Contracts;

namespace LinuxWebTool.Infrastructure.Features.Workers.Adapters;

/// <summary>
/// 进程启动规格定义。
/// </summary>
public sealed record WorkerStartSpec
{
    public required string FileName { get; init; }
    public required string Arguments { get; init; }
    public required string WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Worker 语言运行时适配器接口。
/// </summary>
public interface IWorkerRuntimeAdapter
{
    WorkerRuntimeType RuntimeType { get; }
    
    /// <summary>
    /// 主可执行文件名称。
    /// </summary>
    string PrimaryExecutable { get; }

    /// <summary>
    /// 候选可执行文件名称列表。
    /// </summary>
    IReadOnlyList<string> CandidateExecutables { get; }

    /// <summary>
    /// 环境自检：检查当前系统是否安装对应解释器或编译器。
    /// </summary>
    bool CheckAvailable(out string? missingReason);

    /// <summary>
    /// 环境自检：检查当前系统是否安装对应解释器或编译器，并返回缺失的可执行程序。
    /// </summary>
    bool CheckAvailable(out string? missingReason, out IReadOnlyList<string> missingRunners);

    /// <summary>
    /// 根据当前操作系统发行版解析安装命令指引。
    /// </summary>
    string ResolveInstallGuide(string distroKey);

    /// <summary>
    /// 构造底层启动参数。
    /// </summary>
    WorkerStartSpec BuildStartSpec(
        WorkerManifest manifest,
        string workerDir,
        int? port,
        IReadOnlyDictionary<string, string>? extraEnv = null);
}
