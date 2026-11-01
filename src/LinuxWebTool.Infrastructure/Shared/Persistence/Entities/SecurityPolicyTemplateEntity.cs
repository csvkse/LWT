namespace LinuxWebTool.Infrastructure.Shared.Persistence.Entities;

/// <summary>
/// 安全策略模板持久化实体
/// </summary>
public sealed class SecurityPolicyTemplateEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsBuiltin { get; set; }
    public string RulesJson { get; set; } = "[]";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
