using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LinuxWebTool.Infrastructure.Features.Plugins.Supervisor;

/// <summary>
/// 插件不可变版本管理与可变别名路由器 (Immutable Revision & Mutable Alias Router)
/// - 将代码/清单固化为不可变版本 (revisions/rev-1.0.0-hash/)；
/// - 提供 aliases.json 别名映射 (如 stable, canary)；
/// - 工作流与在途长任务锁定具体 Revision，新请求流向最新别名，消除执行语义漂移。
/// </summary>
public static class PluginRevisionManager
{
    private const string RevisionsDirName = "revisions";
    private const string AliasesFileName = "aliases.json";
    private const string CurrentPointerFileName = "current.rev";

    /// <summary>
    /// 解析指定插件的有效代码目录（支持别名路由解析与不可变版本回溯）。
    /// </summary>
    public static string ResolveEffectiveDirectory(string basePluginDir, string? aliasOrRevision = null)
    {
        var revisionsDir = Path.Combine(basePluginDir, RevisionsDirName);
        if (!Directory.Exists(revisionsDir))
        {
            // 向后兼容传统单目录结构
            return basePluginDir;
        }

        var targetRev = aliasOrRevision;

        // 若未显式指定版本，解析 aliases.json 或 current.rev
        if (string.IsNullOrWhiteSpace(targetRev) || targetRev.Equals("stable", StringComparison.OrdinalIgnoreCase))
        {
            targetRev = ResolveAlias(basePluginDir, "stable") ?? ReadCurrentRevision(basePluginDir);
        }
        else
        {
            var resolved = ResolveAlias(basePluginDir, targetRev);
            if (!string.IsNullOrEmpty(resolved))
            {
                targetRev = resolved;
            }
        }

        if (!string.IsNullOrWhiteSpace(targetRev))
        {
            var revPath = Path.Combine(revisionsDir, targetRev);
            if (Directory.Exists(revPath))
            {
                return revPath;
            }
        }

        // 默认回退到最新修改的版本或基目录
        var subDirs = Directory.GetDirectories(revisionsDir);
        if (subDirs.Length > 0)
        {
            Array.Sort(subDirs, StringComparer.Ordinal);
            return subDirs[^1];
        }

        return basePluginDir;
    }

    /// <summary>
    /// 获取当前插件所有的不可变版本列表。
    /// </summary>
    public static IReadOnlyList<string> ListRevisions(string basePluginDir)
    {
        var revisionsDir = Path.Combine(basePluginDir, RevisionsDirName);
        if (!Directory.Exists(revisionsDir)) return [];

        return Directory.GetDirectories(revisionsDir)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .ToList()!;
    }

    /// <summary>
    /// 固化并创建一个新的不可变版本。
    /// </summary>
    public static string CreateRevision(string basePluginDir, string version, string manifestJson)
    {
        var revisionsDir = Path.Combine(basePluginDir, RevisionsDirName);
        Directory.CreateDirectory(revisionsDir);

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifestJson)))[..8].ToLowerInvariant();
        var revName = $"rev-{version}-{hash}";
        var targetDir = Path.Combine(revisionsDir, revName);
        Directory.CreateDirectory(targetDir);

        // 设置当前活动指针
        SetCurrentRevision(basePluginDir, revName);
        SetAlias(basePluginDir, "stable", revName);

        return revName;
    }

    /// <summary>
    /// 设置或切换别名映射（例如将 canary 指向指定 revision）。
    /// </summary>
    public static void SetAlias(string basePluginDir, string alias, string revisionName)
    {
        var aliasesFile = Path.Combine(basePluginDir, AliasesFileName);
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (File.Exists(aliasesFile))
        {
            try
            {
                var json = File.ReadAllText(aliasesFile);
                var existing = JsonSerializer.Deserialize(json, Adapters.PluginJsonContext.Default.DictionaryStringString);
                if (existing != null)
                {
                    foreach (var (k, v) in existing) aliases[k] = v;
                }
            }
            catch { }
        }

        aliases[alias] = revisionName;
        try
        {
            File.WriteAllText(aliasesFile, JsonSerializer.Serialize(aliases, Adapters.PluginJsonContext.Default.DictionaryStringString));
        }
        catch { }
    }

    private static string? ResolveAlias(string basePluginDir, string alias)
    {
        var aliasesFile = Path.Combine(basePluginDir, AliasesFileName);
        if (!File.Exists(aliasesFile)) return null;

        try
        {
            var json = File.ReadAllText(aliasesFile);
            var aliases = JsonSerializer.Deserialize(json, Adapters.PluginJsonContext.Default.DictionaryStringString);
            if (aliases != null && aliases.TryGetValue(alias, out var rev))
            {
                return rev;
            }
        }
        catch { }

        return null;
    }

    private static void SetCurrentRevision(string basePluginDir, string revisionName)
    {
        var currentFile = Path.Combine(basePluginDir, CurrentPointerFileName);
        try
        {
            File.WriteAllText(currentFile, revisionName);
        }
        catch { }
    }

    private static string? ReadCurrentRevision(string basePluginDir)
    {
        var currentFile = Path.Combine(basePluginDir, CurrentPointerFileName);
        if (!File.Exists(currentFile)) return null;

        try
        {
            var text = File.ReadAllText(currentFile).Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }
}
