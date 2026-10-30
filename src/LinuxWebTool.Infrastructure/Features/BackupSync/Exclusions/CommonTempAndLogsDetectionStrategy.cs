using LinuxWebTool.Contracts.Features.BackupSync.Contracts;

namespace LinuxWebTool.Infrastructure.Features.BackupSync.Exclusions;

/// <summary>
/// 通用操作系统、运行时日志与临时缓存探测策略
/// </summary>
public sealed class CommonTempAndLogsDetectionStrategy : IExcludeDetectionStrategy
{
    public string StrategyName => "通用临时与日志缓存探测";
    public string Category => "通用临时与缓存";
    public int Priority => 50;

    public IEnumerable<ExcludeRecommendationItem> Probe(string sourceRoot, int maxDepth, CancellationToken ct)
    {
        var recommendations = new List<ExcludeRecommendationItem>();
        if (!Directory.Exists(sourceRoot)) return recommendations;

        var matchedLogs = new List<string>();
        var matchedTmp = new List<string>();

        try
        {
            if (Directory.Exists(Path.Combine(sourceRoot, "tmp"))) matchedTmp.Add("tmp");
            if (Directory.Exists(Path.Combine(sourceRoot, "temp"))) matchedTmp.Add("temp");
            if (Directory.Exists(Path.Combine(sourceRoot, "logs"))) matchedLogs.Add("logs");

            // 浅层嗅探是否有 .log 或 .tmp 文件
            foreach (var f in Directory.EnumerateFiles(sourceRoot, "*.*").Take(20))
            {
                if (f.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) matchedLogs.Add(Path.GetFileName(f));
                if (f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) matchedTmp.Add(Path.GetFileName(f));
            }
        }
        catch (Exception) { }

        recommendations.Add(new ExcludeRecommendationItem
        {
            Pattern = "*.log",
            Category = Category,
            Reason = "应用运行时产生的文本或循环日志，变动频繁且体积大",
            MatchedPaths = matchedLogs,
            IsRecommended = true,
            ImpactLevel = "High"
        });

        recommendations.Add(new ExcludeRecommendationItem
        {
            Pattern = "*.tmp",
            Category = Category,
            Reason = "临时工作文件与未完成下载缓存",
            MatchedPaths = matchedTmp,
            IsRecommended = true,
            ImpactLevel = "Medium"
        });

        recommendations.Add(new ExcludeRecommendationItem
        {
            Pattern = "tmp",
            Category = Category,
            Reason = "通用临时中转文件夹",
            MatchedPaths = matchedTmp.Where(p => p == "tmp").ToList(),
            IsRecommended = true,
            ImpactLevel = "Medium"
        });

        recommendations.Add(new ExcludeRecommendationItem
        {
            Pattern = ".DS_Store",
            Category = Category,
            Reason = "macOS 目录显示属性与索引元数据文件",
            MatchedPaths = File.Exists(Path.Combine(sourceRoot, ".DS_Store")) ? [".DS_Store"] : [],
            IsRecommended = true,
            ImpactLevel = "Low"
        });

        recommendations.Add(new ExcludeRecommendationItem
        {
            Pattern = "Thumbs.db",
            Category = Category,
            Reason = "Windows 缩略图缓存数据库",
            MatchedPaths = File.Exists(Path.Combine(sourceRoot, "Thumbs.db")) ? ["Thumbs.db"] : [],
            IsRecommended = true,
            ImpactLevel = "Low"
        });

        return recommendations;
    }
}
