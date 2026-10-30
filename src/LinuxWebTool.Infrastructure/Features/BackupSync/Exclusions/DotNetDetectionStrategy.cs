using LinuxWebTool.Contracts.Features.BackupSync.Contracts;

namespace LinuxWebTool.Infrastructure.Features.BackupSync.Exclusions;

/// <summary>
/// .NET / C# 编译产物与工程缓存探测策略
/// </summary>
public sealed class DotNetDetectionStrategy : IExcludeDetectionStrategy
{
    public string StrategyName => ".NET & C# 编译产物探测";
    public string Category => ".NET / C# 编译产物";
    public int Priority => 20;

    public IEnumerable<ExcludeRecommendationItem> Probe(string sourceRoot, int maxDepth, CancellationToken ct)
    {
        var recommendations = new List<ExcludeRecommendationItem>();
        if (!Directory.Exists(sourceRoot)) return recommendations;

        var matchedBin = new List<string>();
        var matchedObj = new List<string>();
        var hasSlnOrCsproj = false;

        try
        {
            hasSlnOrCsproj = Directory.EnumerateFiles(sourceRoot, "*.*")
                .Any(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".props", StringComparison.OrdinalIgnoreCase));

            if (Directory.Exists(Path.Combine(sourceRoot, "bin"))) matchedBin.Add("bin");
            if (Directory.Exists(Path.Combine(sourceRoot, "obj"))) matchedObj.Add("obj");

            foreach (var sub in Directory.EnumerateDirectories(sourceRoot))
            {
                ct.ThrowIfCancellationRequested();
                var subName = Path.GetFileName(sub);
                if (subName.StartsWith('.')) continue;

                if (Directory.Exists(Path.Combine(sub, "bin"))) matchedBin.Add($"{subName}/bin");
                if (Directory.Exists(Path.Combine(sub, "obj"))) matchedObj.Add($"{subName}/obj");
            }
        }
        catch (Exception) { }

        if (hasSlnOrCsproj || matchedBin.Count > 0 || matchedObj.Count > 0)
        {
            recommendations.Add(new ExcludeRecommendationItem
            {
                Pattern = "bin",
                Category = Category,
                Reason = ".NET 编译生成的二进制输出目录（包含大量临时 dll 与 pdb）",
                MatchedPaths = matchedBin,
                IsRecommended = true,
                ImpactLevel = "High"
            });
            recommendations.Add(new ExcludeRecommendationItem
            {
                Pattern = "obj",
                Category = Category,
                Reason = ".NET 增量编译生成的中间对象产物",
                MatchedPaths = matchedObj,
                IsRecommended = true,
                ImpactLevel = "High"
            });
            recommendations.Add(new ExcludeRecommendationItem
            {
                Pattern = ".vs",
                Category = Category,
                Reason = "Visual Studio 工作区本地状态与 SQLite 缓存",
                MatchedPaths = Directory.Exists(Path.Combine(sourceRoot, ".vs")) ? [".vs"] : [],
                IsRecommended = true,
                ImpactLevel = "Medium"
            });
            recommendations.Add(new ExcludeRecommendationItem
            {
                Pattern = "TestResults",
                Category = Category,
                Reason = "测试运行报告与代码覆盖率临时产物",
                MatchedPaths = Directory.Exists(Path.Combine(sourceRoot, "TestResults")) ? ["TestResults"] : [],
                IsRecommended = true,
                ImpactLevel = "Medium"
            });
        }

        return recommendations;
    }
}
