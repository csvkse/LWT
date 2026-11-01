using System.Collections.Concurrent;
using LinuxWebTool.Contracts.Features.Security.Contracts;
using LinuxWebTool.Infrastructure.Shared.Support;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Security.Policies;

public sealed class SecurityPolicyEngine(
    ISecurityPolicyTemplateStore store,
    ILogger<SecurityPolicyEngine> logger) : ISecurityPolicyEngine
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<SecurityPolicyRule>> _cache = new();

    public void InvalidateCache(string? apiKeyId = null)
    {
        if (string.IsNullOrEmpty(apiKeyId))
        {
            _cache.Clear();
        }
        else
        {
            _cache.TryRemove(apiKeyId, out _);
        }
    }

    public async ValueTask<PolicyDecision> EvaluateAsync(SecurityEvaluationContext context)
    {
        if (!_cache.TryGetValue(context.ApiKeyId, out var rules))
        {
            rules = await store.GetEffectiveRulesForApiKeyAsync(context.ApiKeyId);
            _cache[context.ApiKeyId] = rules;
        }

        var decision = EvaluateCore(rules, context);
        if (!decision.IsAllowed)
        {
            logger.LogWarning("Security policy denied: ApiKey={ApiKeyId}, Domain={Domain}, Reason={Reason}", context.ApiKeyId, context.Domain, decision.DeniedReason);
        }
        return decision;
    }

    public PolicyDecision Simulate(IReadOnlyList<SecurityPolicyRule> rules, SecurityEvaluationContext context)
    {
        return EvaluateCore(rules, context);
    }

    private static PolicyDecision EvaluateCore(IReadOnlyList<SecurityPolicyRule> rules, SecurityEvaluationContext context)
    {
        if (rules.Count == 0)
        {
            return PolicyDecision.Allow();
        }

        var domainRules = rules.Where(r => (r.Domain & context.Domain) != 0).ToList();
        if (domainRules.Count == 0)
        {
            return PolicyDecision.Allow();
        }

        if ((context.Domain & SecurityTargetDomain.AllCommands) != 0)
        {
            return EvaluateCommands(context, domainRules);
        }

        if ((context.Domain & SecurityTargetDomain.AllFiles) != 0)
        {
            return EvaluateFiles(context, domainRules);
        }

        return PolicyDecision.Allow();
    }

    private static PolicyDecision EvaluateCommands(SecurityEvaluationContext context, List<SecurityPolicyRule> rules)
    {
        var rawCmd = context.RawCommandOrPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawCmd))
        {
            return PolicyDecision.Deny("指令不能为空", "EmptyCommandGuard");
        }

        // 1. 动态子 Shell 展开与反引号注入检查
        foreach (var rule in rules.Where(r => r.DisallowSubshells))
        {
            if (ShellCommandLexer.HasSubshellInjection(rawCmd.AsSpan()))
            {
                return PolicyDecision.Deny(
                    "安全策略严禁使用反引号或 $(...) 动态子 Shell 展开",
                    rule.Name,
                    "请使用标准的静态参数调用，杜绝子 Shell 动态计算与注入。");
            }
        }

        // 2. 复合指令链连接检查
        var segments = ShellCommandLexer.SplitCommandSegments(rawCmd);
        foreach (var rule in rules.Where(r => r.DisallowCommandChaining))
        {
            if (segments.Count > 1)
            {
                return PolicyDecision.Deny(
                    "安全策略严禁使用 ;, &&, || 或管道符号执行复合多命令",
                    rule.Name,
                    "请将复合指令拆解为多次单指令依次调用。");
            }
        }

        // 3. 逐个命令段校验
        var allowRules = rules.Where(r => r.Effect == PolicyEffect.Allow && !string.IsNullOrWhiteSpace(r.CommandPatterns)).ToList();

        foreach (var seg in segments)
        {
            var (bin, sub) = ShellCommandLexer.ExtractBinaryAndSubCommand(seg);

            // A. 显式 Deny 先决
            foreach (var denyRule in rules.Where(r => r.Effect == PolicyEffect.Deny))
            {
                if (IsCommandMatch(bin, sub, denyRule.CommandPatterns))
                {
                    return PolicyDecision.Deny($"命令 '{bin} {sub}' 命中安全黑名单", denyRule.Name);
                }
            }

            // B. 容器提权与逃逸标志拦截
            foreach (var rule in rules.Where(r => r.BlockHighRiskFlags))
            {
                if (bin is "docker" or "podman")
                {
                    if (seg.Contains("--privileged", StringComparison.OrdinalIgnoreCase) ||
                        seg.Contains("-v /:", StringComparison.OrdinalIgnoreCase) ||
                        seg.Contains("--volume /:", StringComparison.OrdinalIgnoreCase) ||
                        seg.Contains("docker.sock", StringComparison.OrdinalIgnoreCase))
                    {
                        return PolicyDecision.Deny(
                            "严禁使用 --privileged 特权模式或挂载宿主机根目录/Docker套接字",
                            rule.Name,
                            "请移除高危容器提权参数后再行调用。");
                    }
                }
            }

            // C. 显式 Allow 白名单匹配
            if (allowRules.Count > 0)
            {
                bool matched = allowRules.Any(r => IsCommandMatch(bin, sub, r.CommandPatterns));
                if (!matched)
                {
                    var allowedList = string.Join("; ", allowRules.Select(r => r.CommandPatterns));
                    return PolicyDecision.Deny(
                        $"命令 '{bin} {sub}' 不在允许执行的命令白名单中",
                        "CommandWhitelistGuard",
                        $"当前 API Key 仅允许执行以下命令对象: [{allowedList}]");
                }
            }
        }

        return PolicyDecision.Allow();
    }

    private static bool IsCommandMatch(string bin, string sub, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        var tokens = pattern.Split([';', ',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            var parts = token.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            var patBin = parts[0].ToLowerInvariant();
            if (patBin != "*" && patBin != bin) continue;
            if (parts.Length == 1 || parts[1] == "*") return true;
            if (parts[1].ToLowerInvariant() == sub) return true;
        }
        return false;
    }

    private static PolicyDecision EvaluateFiles(SecurityEvaluationContext context, List<SecurityPolicyRule> rules)
    {
        var targetPath = context.RawCommandOrPath;
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return PolicyDecision.Deny("文件路径不能为空", "EmptyPathGuard");
        }

        // 区分远程 Linux SFTP 路径与本地宿主路径
        var isRemote = (context.Domain & SecurityTargetDomain.RemoteFile) != 0;
        string normalized;
        try
        {
            normalized = isRemote
                ? PosixPathNormalizer.Normalize(targetPath)
                : FileBrowserPath.Normalize(targetPath);
        }
        catch (Exception ex)
        {
            return PolicyDecision.Deny($"路径格式非法: {ex.Message}", "PathNormalizer");
        }

        var action = context.ActionVerb.ToLowerInvariant();
        bool isWriteOrDelete = action is "write" or "delete" or "mkdir" or "rename" or "upload" or "move";

        // 1. 只读保护模式拦截
        foreach (var rule in rules.Where(r => r.ReadOnlyOnly))
        {
            if (isWriteOrDelete)
            {
                return PolicyDecision.Deny(
                    $"当前策略处于只读防护模式，严禁写入、删除或重命名操作 (尝试动作={action})",
                    rule.Name,
                    "该 API Key 仅被授予只读浏览/下载权限。");
            }
        }

        // 2. 路径白名单范围检查
        var allowRules = rules.Where(r => r.Effect == PolicyEffect.Allow && !string.IsNullOrWhiteSpace(r.PathPatterns)).ToList();
        if (allowRules.Count > 0)
        {
            bool withinAny = false;
            foreach (var rule in allowRules)
            {
                var roots = isRemote
                    ? PosixPathNormalizer.ParseAllowedRoots(rule.PathPatterns)
                    : PathScopeGuard.GetAllowedRoots(rule.PathPatterns);

                if (roots.Contains("*") || roots.Any(root => isRemote
                    ? PosixPathNormalizer.IsWithin(normalized, root)
                    : FileBrowserPath.IsWithin(normalized, root)))
                {
                    withinAny = true;
                    break;
                }
            }

            if (!withinAny)
            {
                var allowedSummary = string.Join(", ", allowRules.Select(r => r.PathPatterns));
                return PolicyDecision.Deny(
                    $"请求路径 '{normalized}' 超出已授权的安全目录边界",
                    "PathScopeGuard",
                    $"请将文件操作限定在已授权的目录范围之内: [{allowedSummary}]");
            }
        }

        return PolicyDecision.Allow();
    }
}
