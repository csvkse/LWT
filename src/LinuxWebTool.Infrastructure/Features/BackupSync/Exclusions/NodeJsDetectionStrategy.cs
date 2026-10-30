using LinuxWebTool.Contracts.Features.BackupSync.Contracts;

namespace LinuxWebTool.Infrastructure.Features.BackupSync.Exclusions;

/// <summary>
/// Node.js / 前端开发与依赖生态智能探测策略
/// </summary>
public sealed class NodeJsDetectionStrategy : IExcludeDetectionStrategy
{
    public string StrategyName => "Node.js & 前端依赖生态探测";
    public string Category => "Node.js / 前端依赖";
    public int Priority => 10;

    public IEnumerable<ExcludeRecommendationItem> Probe(string sourceRoot, int maxDepth, CancellationToken ct)
    {
        var recommendations = new List<ExcludeRecommendationItem>();
        if (!Directory.Exists(sourceRoot)) return recommendations;

        // 1. 嗅探是否有 node 生态标志文件
        var hasPackageJson = File.Exists(Path.Combine(sourceRoot, "package.json"));
        var hasPnpmLock = File.Exists(Path.Combine(sourceRoot, "pnpm-lock.yaml"));
        var hasDsh = Directory.Exists(Path.Combine(sourceRoot, ".dsh")) || File.Exists(Path.Combine(sourceRoot, "settings.yaml.imported"));

        // 2. 浅层搜寻常见依赖目录（最大深度 ≤ 2）
        var matchedNodeModules = new List<string>();
        var matchedPnpm = new List<string>();
        var matchedCaches = new List<string>();

        try
        {
            // 根目录直接探针
            if (Directory.Exists(Path.Combine(sourceRoot, "node_modules")))
                matchedNodeModules.Add("node_modules");

            if (Directory.Exists(Path.Combine(sourceRoot, ".cache")))
                matchedCaches.Add(".cache");

            if (Directory.Exists(Path.Combine(sourceRoot, ".local/share/pnpm")) || Directory.Exists(Path.Combine(sourceRoot, ".pnpm-store")))
                matchedPnpm.Add(".local/share/pnpm");

            // 第 1 级子目录探针（浅层，防卡死）
            foreach (var sub in Directory.EnumerateDirectories(sourceRoot))
            {
                ct.ThrowIfCancellationRequested();
                var subName = Path.GetFileName(sub);
                if (subName.StartsWith('.'))
                {
                    if (subName == ".dsh" && Directory.Exists(Path.Combine(sub, "profiles/web/node_modules")))
                        matchedNodeModules.Add(".dsh/profiles/web/node_modules");
                    continue;
                }

                var subNodeModules = Path.Combine(sub, "node_modules");
                if (Directory.Exists(subNodeModules))
                {
                    matchedNodeModules.Add($"{subName}/node_modules");
                }
            }
        }
        catch (Exception) { }

        if (hasPackageJson || hasPnpmLock || hasDsh || matchedNodeModules.Count > 0)
        {
            recommendations.Add(new ExcludeRecommendationItem
            {
                Pattern = "node_modules",
                Category = Category,
                Reason = "Node.js / 前端依赖包目录，包含海量可重新生成的依赖包，极易拖垮同步速度",
                MatchedPaths = matchedNodeModules,
                IsRecommended = true,
                ImpactLevel = "High"
            });
        }

        if (matchedPnpm.Count > 0 || hasPnpmLock)
        {
            recommendations.Add(new ExcludeRecommendationItem
            {
                Pattern = ".local/share/pnpm",
                Category = Category,
                Reason = "pnpm 本地或全局硬链接包存储库，存在大量重复索引",
                MatchedPaths = matchedPnpm,
                IsRecommended = true,
                ImpactLevel = "High"
            });
            recommendations.Add(new ExcludeRecommendationItem
            {
                Pattern = ".pnpm-store",
                Category = Category,
                Reason = "pnpm 集中虚拟存储区",
                MatchedPaths = [],
                IsRecommended = true,
                ImpactLevel = "High"
            });
        }

        if (matchedCaches.Count > 0 || hasPackageJson || hasDsh)
        {
            recommendations.Add(new ExcludeRecommendationItem
            {
                Pattern = ".cache",
                Category = Category,
                Reason = "前端打包编译器（Vite/Webpack/Babel）产生的瞬态构建缓存",
                MatchedPaths = matchedCaches,
                IsRecommended = true,
                ImpactLevel = "High"
            });
        }

        return recommendations;
    }
}
