using LinuxWebTool.Contracts.Features.BackupSync.Contracts;

namespace LinuxWebTool.Infrastructure.Features.BackupSync.Exclusions;

/// <summary>
/// 技术栈与文件排除智能探测策略接口（策略模式）
/// </summary>
public interface IExcludeDetectionStrategy
{
    string StrategyName { get; }
    string Category { get; }
    int Priority { get; }

    /// <summary>
    /// 对目标根路径进行浅层探针嗅探（最大深度通常 ≤ 2，支持超时与取消）
    /// </summary>
    IEnumerable<ExcludeRecommendationItem> Probe(string sourceRoot, int maxDepth, CancellationToken ct);
}
