namespace LinuxWebTool.Infrastructure.Shared.Support;

/// <summary>
/// API Key 访问路径边界防护守卫（纯领域安全规则，抗 ../，抗符号链接 / Junction 逃逸）：
/// - 支持配置单个或多个允许路径（支持分号或换行分隔）；
/// - 配置为空时表示不限制（全量访问）；
/// - 严格按路径分量匹配，拒绝前缀误穿透（如 /workspace2 穿透 /workspace）；
/// - 深度解析物理真实路径，阻断符号链接逃逸至非授权敏感目录。
/// </summary>
public static class PathScopeGuard
{
    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// 解析已配置的合法根路径列表（全部经过规范化）
    /// </summary>
    public static IReadOnlyList<string> GetAllowedRoots(string? allowedPathsConfig)
    {
        if (string.IsNullOrWhiteSpace(allowedPathsConfig))
        {
            return Array.Empty<string>();
        }

        var list = new List<string>();
        var items = allowedPathsConfig.Split([';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item)) continue;
            try
            {
                var normalized = FileBrowserPath.Normalize(item);
                if (!list.Contains(normalized, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
                {
                    list.Add(normalized);
                }
            }
            catch
            {
                // 忽略配置中个别无法规范化的非法字符路径
            }
        }

        return list;
    }

    /// <summary>
    /// 判定目标路径是否落在 API Key 允许的路径范围内
    /// </summary>
    public static bool IsAllowed(string targetPath, string? allowedPathsConfig, out string? failureReason)
    {
        // 1. 未配置限定路径：代表拥有全局访问权限
        if (string.IsNullOrWhiteSpace(allowedPathsConfig))
        {
            failureReason = null;
            return true;
        }

        var roots = GetAllowedRoots(allowedPathsConfig);
        if (roots.Count == 0)
        {
            failureReason = null;
            return true;
        }

        // 2. 词法规范化
        string normalizedTarget;
        try
        {
            normalizedTarget = FileBrowserPath.Normalize(targetPath);
        }
        catch (Exception ex)
        {
            failureReason = $"路径不合法: {ex.Message}";
            return false;
        }

        // 3. 词法分量包含检查
        var matchedRoot = roots.FirstOrDefault(root => FileBrowserPath.IsWithin(normalizedTarget, root));
        if (matchedRoot is null)
        {
            failureReason = $"访问被拒绝：请求路径 '{normalizedTarget}' 超出该 API Key 的授权范围 ({string.Join(", ", roots)})";
            return false;
        }

        // 4. 符号链接与 Junction 逃逸安全检测
        if (File.Exists(normalizedTarget) || Directory.Exists(normalizedTarget))
        {
            try
            {
                FileSystemInfo fsi = Directory.Exists(normalizedTarget) ? new DirectoryInfo(normalizedTarget) : new FileInfo(normalizedTarget);
                if (fsi.LinkTarget is not null)
                {
                    var resolved = fsi.ResolveLinkTarget(returnFinalTarget: true);
                    if (resolved is not null)
                    {
                        var normalizedResolved = FileBrowserPath.Normalize(resolved.FullName);
                        if (!roots.Any(root => FileBrowserPath.IsWithin(normalizedResolved, root)))
                        {
                            failureReason = $"访问被拒绝：检测到指向授权范围外部的符号链接目标 '{normalizedResolved}'";
                            return false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                failureReason = $"解析路径真实链接目标失败: {ex.Message}";
                return false;
            }
        }

        failureReason = null;
        return true;
    }
}
