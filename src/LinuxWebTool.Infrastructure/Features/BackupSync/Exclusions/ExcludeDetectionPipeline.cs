using LinuxWebTool.Contracts.Features.BackupSync.Contracts;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.BackupSync.Exclusions;

/// <summary>
/// 智能排除规则嗅探管道（管道/外观模式）
/// 编排所有注册的嗅探策略，执行带软超时的并行/顺序探测，并返回推荐规则与常用预设
/// </summary>
public sealed class ExcludeDetectionPipeline
{
    private readonly IEnumerable<IExcludeDetectionStrategy> _strategies;
    private readonly ILogger<ExcludeDetectionPipeline> _logger;

    public ExcludeDetectionPipeline(
        IEnumerable<IExcludeDetectionStrategy> strategies,
        ILogger<ExcludeDetectionPipeline> logger)
    {
        _strategies = strategies.OrderBy(s => s.Priority);
        _logger = logger;
    }

    /// <summary>
    /// 对目标路径执行浅层探针嗅探并组装建议列表与预设集
    /// 默认软超时 2.5 秒，防止大规模慢速存储挂起
    /// </summary>
    public ProbeExcludesResponse ProbePath(string sourcePath, CancellationToken ct = default)
    {
        var presets = GetDefaultPresets();

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return new ProbeExcludesResponse
            {
                Success = false,
                Message = "指定的探测路径为空",
                ScannedPath = string.Empty,
                PresetTemplates = presets
            };
        }

        var normalizedPath = Path.GetFullPath(sourcePath.Trim());
        if (!Directory.Exists(normalizedPath))
        {
            return new ProbeExcludesResponse
            {
                Success = false,
                Message = $"目录不存在或无访问权限: {normalizedPath}",
                ScannedPath = normalizedPath,
                PresetTemplates = presets
            };
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(2.5));

        var allRecommendations = new List<ExcludeRecommendationItem>();
        var detectedStacks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var strategy in _strategies)
        {
            if (cts.IsCancellationRequested)
            {
                _logger.LogWarning("排除规则探测达到软超时限制(2.5s)，中断后续策略执行: {Path}", normalizedPath);
                break;
            }

            try
            {
                var items = strategy.Probe(normalizedPath, maxDepth: 2, cts.Token).ToList();
                if (items.Count > 0)
                {
                    detectedStacks.Add(strategy.Category);
                    allRecommendations.AddRange(items);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("策略 {Strategy} 探测超时", strategy.StrategyName);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "策略 {Strategy} 执行发生异常", strategy.StrategyName);
            }
        }

        // 去重规则：Pattern 唯一，优先保留推荐度高/影响级别高的项
        var deduplicated = allRecommendations
            .GroupBy(r => r.Pattern.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.First();
                var matched = g.SelectMany(x => x.MatchedPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return new ExcludeRecommendationItem
                {
                    Pattern = first.Pattern,
                    Category = first.Category,
                    Reason = first.Reason,
                    MatchedPaths = matched,
                    IsRecommended = g.Any(x => x.IsRecommended),
                    ImpactLevel = g.Any(x => x.ImpactLevel == "High") ? "High" : first.ImpactLevel
                };
            })
            .OrderByDescending(r => r.ImpactLevel == "High")
            .ThenBy(r => r.Pattern)
            .ToList();

        return new ProbeExcludesResponse
        {
            Success = true,
            Message = $"成功探测并识别 {detectedStacks.Count} 种技术栈/特征，推荐 {deduplicated.Count} 条排除规则",
            ScannedPath = normalizedPath,
            DetectedTechStacks = detectedStacks.ToList(),
            Recommendations = deduplicated,
            PresetTemplates = presets
        };
    }

    /// <summary>
    /// 获取平台标准常用排除规则预设集
    /// </summary>
    public static List<ExcludePresetTemplate> GetDefaultPresets()
    {
        return
        [
            new ExcludePresetTemplate
            {
                Name = "Node.js 前端 / 全栈",
                Description = "排除 node_modules、构建输出、包管理器缓存与日志",
                Patterns = ["node_modules/", ".next/", ".nuxt/", "dist/", ".turbo/", "npm-debug.log*", "yarn-error.log*", "pnpm-debug.log*", ".pnpm-store/"]
            },
            new ExcludePresetTemplate
            {
                Name = ".NET / C# 编译环境",
                Description = "排除 bin、obj、VS 缓存与单元测试产物",
                Patterns = ["bin/", "obj/", ".vs/", "*.user", "*.suo", "TestResults/"]
            },
            new ExcludePresetTemplate
            {
                Name = "Python 解释器环境",
                Description = "排除 __pycache__、字节码文件及虚拟环境目录",
                Patterns = ["__pycache__/", "*.pyc", "*.pyo", ".venv/", "venv/", "env/", ".pytest_cache/", ".mypy_cache/"]
            },
            new ExcludePresetTemplate
            {
                Name = "版本控制 (VCS) 隐藏目录",
                Description = "排除 .git、.svn、.hg 版本库元数据目录",
                Patterns = [".git/", ".svn/", ".hg/"]
            },
            new ExcludePresetTemplate
            {
                Name = "通用系统临时与日志文件",
                Description = "排除 *.log 日志、*.tmp 临时文件及系统缓存 Thumbs.db / .DS_Store",
                Patterns = ["*.log", "*.tmp", "*.temp", "tmp/", "temp/", ".DS_Store", "Thumbs.db"]
            },
            new ExcludePresetTemplate
            {
                Name = "Java / Gradle / Maven",
                Description = "排除 target/、.gradle/、build/ 及 class 文件",
                Patterns = ["target/", ".gradle/", "build/", "*.class"]
            }
        ];
    }
}
