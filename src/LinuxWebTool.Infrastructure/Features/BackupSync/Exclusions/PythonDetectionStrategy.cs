using LinuxWebTool.Contracts.Features.BackupSync.Contracts;

namespace LinuxWebTool.Infrastructure.Features.BackupSync.Exclusions;

/// <summary>
/// Python 虚拟环境与字节码缓存探测策略
/// </summary>
public sealed class PythonDetectionStrategy : IExcludeDetectionStrategy
{
    public string StrategyName => "Python 虚拟环境与缓存探测";
    public string Category => "Python 运行时与依赖";
    public int Priority => 30;

    public IEnumerable<ExcludeRecommendationItem> Probe(string sourceRoot, int maxDepth, CancellationToken ct)
    {
        var recommendations = new List<ExcludeRecommendationItem>();
        if (!Directory.Exists(sourceRoot)) return recommendations;

        var hasPyProject = File.Exists(Path.Combine(sourceRoot, "requirements.txt"))
            || File.Exists(Path.Combine(sourceRoot, "pyproject.toml"))
            || File.Exists(Path.Combine(sourceRoot, "setup.py"));

        var matchedPyCache = new List<string>();
        var matchedVenv = new List<string>();

        try
        {
            if (Directory.Exists(Path.Combine(sourceRoot, "__pycache__"))) matchedPyCache.Add("__pycache__");
            if (Directory.Exists(Path.Combine(sourceRoot, ".venv"))) matchedVenv.Add(".venv");
            if (Directory.Exists(Path.Combine(sourceRoot, "venv"))) matchedVenv.Add("venv");

            foreach (var sub in Directory.EnumerateDirectories(sourceRoot))
            {
                ct.ThrowIfCancellationRequested();
                var subName = Path.GetFileName(sub);
                if (Directory.Exists(Path.Combine(sub, "__pycache__"))) matchedPyCache.Add($"{subName}/__pycache__");
                if (subName is ".venv" or "venv") matchedVenv.Add(subName);
            }
        }
        catch (Exception) { }

        if (hasPyProject || matchedPyCache.Count > 0 || matchedVenv.Count > 0)
        {
            recommendations.Add(new ExcludeRecommendationItem
            {
                Pattern = "__pycache__",
                Category = Category,
                Reason = "Python 解释器预编译生成的 .pyc 字节码缓存目录",
                MatchedPaths = matchedPyCache,
                IsRecommended = true,
                ImpactLevel = "High"
            });
            recommendations.Add(new ExcludeRecommendationItem
            {
                Pattern = "*.pyc",
                Category = Category,
                Reason = "Python 字节码编译文件",
                MatchedPaths = [],
                IsRecommended = true,
                ImpactLevel = "High"
            });
            recommendations.Add(new ExcludeRecommendationItem
            {
                Pattern = ".venv",
                Category = Category,
                Reason = "Python 虚拟隔离环境（建议通过 requirements.txt 恢复，无需打包海量包）",
                MatchedPaths = matchedVenv,
                IsRecommended = true,
                ImpactLevel = "High"
            });
            recommendations.Add(new ExcludeRecommendationItem
            {
                Pattern = "venv",
                Category = Category,
                Reason = "Python 虚拟环境目录",
                MatchedPaths = [],
                IsRecommended = true,
                ImpactLevel = "High"
            });
        }

        return recommendations;
    }
}
