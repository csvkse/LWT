namespace LinuxWebTool.Contracts.Features.Security.Contracts;

/// <summary>
/// 安全策略保护目标领域（支持位运算复合匹配）
/// </summary>
[Flags]
public enum SecurityTargetDomain
{
    None = 0,
    RemoteCommand = 1 << 0,  // 1
    LocalTerminal = 1 << 1,  // 2
    LocalCommand = LocalTerminal,
    RemoteFile = 1 << 2,     // 4
    LocalFile = 1 << 3,      // 8
    McpTool = 1 << 4,        // 16

    AllCommands = RemoteCommand | LocalTerminal, // 3
    AllFiles = RemoteFile | LocalFile,          // 12
    All = AllCommands | AllFiles | McpTool       // 31
}

/// <summary>
/// 策略效果（显式 Deny 先决）
/// </summary>
public enum PolicyEffect
{
    Deny = 0,
    Allow = 1
}

/// <summary>
/// 业务能力风险等级（用于前端快速安全预设）
/// </summary>
public enum RiskLevel
{
    ReadOnly = 1,
    Write = 2,
    Dangerous = 3,
    Critical = 4
}

/// <summary>
/// 单条安全策略规则（可序列化为 JSON，支持 AOT）
/// </summary>
public sealed record SecurityPolicyRule(
    string RuleId,
    string Name,
    SecurityTargetDomain Domain,
    PolicyEffect Effect,
    string? CommandPatterns = null,
    string? PathPatterns = null,
    bool DisallowSubshells = false,
    bool DisallowCommandChaining = false,
    bool BlockHighRiskFlags = false,
    bool ReadOnlyOnly = false
);

/// <summary>
/// 安全策略模板列表项响应
/// </summary>
public sealed record SecurityPolicyTemplateItemResponse(
    string Id,
    string Name,
    string? Description,
    bool IsBuiltin,
    string RulesJson,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int AttachedKeyCount = 0
);

/// <summary>
/// 创建安全策略模板请求
/// </summary>
public sealed record CreateSecurityPolicyTemplateRequest(
    string Name,
    string? Description,
    string RulesJson
);

/// <summary>
/// 更新安全策略模板请求
/// </summary>
public sealed record UpdateSecurityPolicyTemplateRequest(
    string Name,
    string? Description,
    string RulesJson
);

/// <summary>
/// 策略即时试运行模拟请求
/// </summary>
public sealed record SimulatePolicyRequest(
    string RulesJson,
    SecurityTargetDomain Domain,
    string TestPayload,
    string? ActionVerb = null
);

/// <summary>
/// 策略即时试运行模拟响应
/// </summary>
public sealed record SimulatePolicyResponse(
    bool IsAllowed,
    string? RuleTriggered = null,
    string? DeniedReason = null,
    string? Suggestion = null
);

/// <summary>
/// 统一业务能力单元清单项（同时映射 REST API 与 MCP 工具）
/// </summary>
public sealed record ModuleCapabilityItem(
    string CapabilityId,
    string Name,
    string Module,
    RiskLevel Risk,
    string RestRoute,
    string McpToolName,
    bool SupportsApi,
    bool SupportsMcp
);

/// <summary>
/// 策略求值判定上下文（不可变领域模型）
/// </summary>
public sealed record SecurityEvaluationContext(
    string ApiKeyId,
    string ApiKeyName,
    SecurityTargetDomain Domain,
    string ActionVerb,             // "Execute", "Read", "Write", "Delete", "Upload", etc.
    string TargetResource,         // HostId, LocalHost, VirtualPath
    string? RawCommandOrPath,
    string ClientIp
);

/// <summary>
/// 策略决断结果
/// </summary>
public sealed record PolicyDecision(
    bool IsAllowed,
    string? DeniedReason = null,
    string? RuleName = null,
    string? Suggestion = null
)
{
    public static PolicyDecision Allow() => new(true);
    public static PolicyDecision Deny(string reason, string ruleName, string? suggestion = null)
        => new(false, reason, ruleName, suggestion);
}

/// <summary>
/// 统一安全策略引擎接口
/// </summary>
public interface ISecurityPolicyEngine
{
    ValueTask<PolicyDecision> EvaluateAsync(SecurityEvaluationContext context);
    void InvalidateCache(string? apiKeyId = null);
    PolicyDecision Simulate(IReadOnlyList<SecurityPolicyRule> rules, SecurityEvaluationContext context);
}

/// <summary>
/// 安全策略模板存储抽象
/// </summary>
public interface ISecurityPolicyTemplateStore
{
    Task<IReadOnlyList<SecurityPolicyTemplateItemResponse>> GetAllAsync();
    Task<SecurityPolicyTemplateItemResponse?> GetByIdAsync(string id);
    Task<string> CreateAsync(CreateSecurityPolicyTemplateRequest request);
    Task<bool> UpdateAsync(string id, UpdateSecurityPolicyTemplateRequest request);
    Task<(bool Success, string? Error)> DeleteAsync(string id);
    Task<IReadOnlyList<SecurityPolicyRule>> GetEffectiveRulesForApiKeyAsync(string apiKeyId);
}
