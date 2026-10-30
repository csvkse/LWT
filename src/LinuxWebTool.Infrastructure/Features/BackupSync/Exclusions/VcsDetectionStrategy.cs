using LinuxWebTool.Contracts.Features.BackupSync.Contracts;

namespace LinuxWebTool.Infrastructure.Features.BackupSync.Exclusions;

/// <summary>
/// 版本控制系统元数据探测策略
/// </summary>
public sealed class VcsDetectionStrategy : IExcludeDetectionStrategy
{
    public string StrategyName => "版本控制元数据探测";
    public string Category => "版本控制系统";
    public int Priority => 40;

    public IEnumerable<ExcludeRecommendationItem> Probe(string sourceRoot, int maxDepth, CancellationToken ct)
    {
        var recommendations = new List<ExcludeRecommendationItem>();
        if (!Directory.Exists(sourceRoot)) return recommendations;

        var hasGit = Directory.Exists(Path.Combine(sourceRoot, ".git"));
        var hasSvn = Directory.Exists(Path.Combine(sourceRoot, ".svn"));
        var hasHg = Directory.Exists(Path.Combine(sourceRoot, ".hg"));

        if (hasGit)
        {
            recommendations.Add(new ExcludeRecommendationItem
            {
                Pattern = ".git",
                Category = Category,
                Reason = "Git 版本控制元数据仓库，包含全量历史对象树",
                MatchedPaths = [".git"],
                IsRecommended = true,
                ImpactLevel = "High"
            });
        }

        if (hasSvn)
        {
            recommendations.Add(new ExcludeRecommendationItem
            {
                Pattern = ".svn",
                Category = Category,
                Reason = "Subversion 版本控制工作区元数据目录",
                MatchedPaths = [".svn"],
                IsRecommended = true,
                ImpactLevel = "Medium"
            });
        }

        if (hasHg)
        {
            recommendations.Add(new ExcludeRecommendationItem
            {
                Pattern = ".hg",
                Category = Category,
                Reason = "Mercurial 版本控制仓库目录",
                MatchedPaths = [".hg"],
                IsRecommended = true,
                ImpactLevel = "Medium"
            });
        }

        return recommendations;
    }
}
