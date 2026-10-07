using System.Text.Json;
using Xunit;

namespace LinuxWebTool.ArchitectureTests.Support;

/// <summary>
/// 架构门禁精确基线判定引擎（对标 governance/gates.md）：
/// - 精确比对：(ruleId, path, target) 三元组；
/// - 防假绿：空扫描显式失败；
/// - 消除即清理（Stale Baseline 必死）：若基线存在未被真实问题命中的条目，必须断言失败要求清理；
/// - 只降不增：新违规若未匹配基线直接阻断。
/// </summary>
public sealed class BaselineEngine
{
    public sealed record BaselineEntry(
        string RuleId,
        string Path,
        string Target,
        string Reason,
        string Owner,
        string CleanupCondition,
        string? Expiry = null);

    private readonly HashSet<string> _registeredKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _hitKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<BaselineEntry> _entries = new();

    public BaselineEngine(string baselineFilePath)
    {
        if (!File.Exists(baselineFilePath))
        {
            return;
        }

        var json = File.ReadAllText(baselineFilePath);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("entries", out var entriesProp))
        {
            return;
        }

        foreach (var item in entriesProp.EnumerateArray())
        {
            var ruleId = item.GetProperty("ruleId").GetString() ?? string.Empty;
            var path = item.GetProperty("path").GetString() ?? string.Empty;
            var target = item.GetProperty("target").GetString() ?? string.Empty;
            var reason = item.TryGetProperty("reason", out var r) ? r.GetString() ?? string.Empty : string.Empty;
            var owner = item.TryGetProperty("owner", out var o) ? o.GetString() ?? string.Empty : string.Empty;
            var cleanup = item.TryGetProperty("cleanupCondition", out var c) ? c.GetString() ?? string.Empty : string.Empty;
            var expiry = item.TryGetProperty("expiry", out var e) ? e.GetString() : null;

            var entry = new BaselineEntry(ruleId, path, target, reason, owner, cleanup, expiry);
            _entries.Add(entry);

            var key = NormalizeKey(ruleId, path, target);
            if (!_registeredKeys.Add(key))
            {
                throw new InvalidOperationException($"基线文件包含重复条目：{key}");
            }
        }
    }

    public static string NormalizeKey(string ruleId, string relativePath, string target)
    {
        var normPath = relativePath.Replace('\\', '/').Trim('/');
        return $"{ruleId.Trim()}|{normPath}|{target.Trim()}";
    }

    public bool IsSuppressed(string ruleId, string relativePath, string target)
    {
        var key = NormalizeKey(ruleId, relativePath, target);
        if (_registeredKeys.Contains(key))
        {
            _hitKeys.Add(key);
            return true;
        }
        return false;
    }

    public void AssertNoStaleEntries()
    {
        var staleKeys = _registeredKeys.Except(_hitKeys).ToList();
        if (staleKeys.Count > 0)
        {
            Assert.Fail(
                "检测到已失效的残留基线条目（技术债务已被修复，必须从 backend-baseline.json 中删除对应条目以防假绿）：\n" +
                string.Join("\n", staleKeys));
        }
    }

    public static void AssertNonEmptyScope(int count, string scopeName)
    {
        if (count == 0)
        {
            Assert.Fail($"门禁扫描范围为空异常：{scopeName} 扫描结果为 0 个文件，可能存在路径错误或配置丢失（防假绿）。");
        }
    }
}
