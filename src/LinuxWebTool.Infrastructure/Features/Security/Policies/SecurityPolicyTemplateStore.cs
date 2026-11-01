using System.Text.Json;
using Dapper;
using LinuxWebTool.Contracts.Features.Security.Contracts;
using LinuxWebTool.Infrastructure.Shared.Persistence;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Security.Policies;

public sealed class SecurityPolicyTemplateStore(
    DbConnectionFactory factory,
    ILogger<SecurityPolicyTemplateStore> logger) : ISecurityPolicyTemplateStore
{
    public async Task<IReadOnlyList<SecurityPolicyTemplateItemResponse>> GetAllAsync()
    {
        using var db = factory.CreateConnection();
        var entities = (await db.QueryAsync<SecurityPolicyTemplateEntity>(
            "SELECT Id, Name, Description, IsBuiltin, RulesJson, CreatedAt, UpdatedAt FROM security_policy_template ORDER BY IsBuiltin DESC, CreatedAt ASC")).ToList();

        // 统计各策略挂载数量
        var keyPolicyStrings = (await db.QueryAsync<string>("SELECT PolicyTemplateIds FROM api_key WHERE PolicyTemplateIds IS NOT NULL AND PolicyTemplateIds != ''")).ToList();
        var usageCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in keyPolicyStrings)
        {
            var ids = ApiKeyService.ParsePolicyTemplateIds(raw);
            if (ids != null)
            {
                foreach (var id in ids)
                {
                    usageCounts[id] = usageCounts.GetValueOrDefault(id, 0) + 1;
                }
            }
        }

        return entities.Select(e => new SecurityPolicyTemplateItemResponse(
            Id: e.Id,
            Name: e.Name,
            Description: e.Description,
            IsBuiltin: e.IsBuiltin,
            RulesJson: e.RulesJson,
            CreatedAt: e.CreatedAt,
            UpdatedAt: e.UpdatedAt,
            AttachedKeyCount: usageCounts.GetValueOrDefault(e.Id, 0)
        )).ToList();
    }

    public async Task<SecurityPolicyTemplateItemResponse?> GetByIdAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        using var db = factory.CreateConnection();
        var entity = await db.QueryFirstOrDefaultAsync<SecurityPolicyTemplateEntity>(
            "SELECT Id, Name, Description, IsBuiltin, RulesJson, CreatedAt, UpdatedAt FROM security_policy_template WHERE Id = @Id COLLATE NOCASE",
            new { Id = id });

        if (entity is null) return null;

        var count = await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM api_key WHERE PolicyTemplateIds LIKE @Pattern",
            new { Pattern = $"%{entity.Id}%" });

        return new SecurityPolicyTemplateItemResponse(
            Id: entity.Id,
            Name: entity.Name,
            Description: entity.Description,
            IsBuiltin: entity.IsBuiltin,
            RulesJson: entity.RulesJson,
            CreatedAt: entity.CreatedAt,
            UpdatedAt: entity.UpdatedAt,
            AttachedKeyCount: count
        );
    }

    public async Task<string> CreateAsync(CreateSecurityPolicyTemplateRequest request)
    {
        var id = $"tpl_{Guid.NewGuid():N}"[..16];
        var now = DateTime.UtcNow;

        using var db = factory.CreateConnection();
        const string sql = @"
            INSERT INTO security_policy_template (Id, Name, Description, IsBuiltin, RulesJson, CreatedAt, UpdatedAt)
            VALUES (@Id, @Name, @Description, 0, @RulesJson, @CreatedAt, @UpdatedAt)";

        await db.ExecuteAsync(sql, new
        {
            Id = id,
            Name = request.Name.Trim(),
            Description = request.Description?.Trim(),
            RulesJson = string.IsNullOrWhiteSpace(request.RulesJson) ? "[]" : request.RulesJson.Trim(),
            CreatedAt = now.ToString("O"),
            UpdatedAt = now.ToString("O")
        });

        return id;
    }

    public async Task<bool> UpdateAsync(string id, UpdateSecurityPolicyTemplateRequest request)
    {
        using var db = factory.CreateConnection();
        var existing = await db.QueryFirstOrDefaultAsync<SecurityPolicyTemplateEntity>(
            "SELECT Id, IsBuiltin FROM security_policy_template WHERE Id = @Id COLLATE NOCASE",
            new { Id = id });

        if (existing is null) return false;

        const string sql = @"
            UPDATE security_policy_template
            SET Name = @Name, Description = @Description, RulesJson = @RulesJson, UpdatedAt = @UpdatedAt
            WHERE Id = @Id COLLATE NOCASE";

        var affected = await db.ExecuteAsync(sql, new
        {
            Id = id,
            Name = request.Name.Trim(),
            Description = request.Description?.Trim(),
            RulesJson = string.IsNullOrWhiteSpace(request.RulesJson) ? "[]" : request.RulesJson.Trim(),
            UpdatedAt = DateTime.UtcNow.ToString("O")
        });

        return affected > 0;
    }

    public async Task<(bool Success, string? Error)> DeleteAsync(string id)
    {
        using var db = factory.CreateConnection();
        var existing = await db.QueryFirstOrDefaultAsync<SecurityPolicyTemplateEntity>(
            "SELECT Id, IsBuiltin FROM security_policy_template WHERE Id = @Id COLLATE NOCASE",
            new { Id = id });

        if (existing is null) return (false, "策略模板不存在");
        if (existing.IsBuiltin)
        {
            return (false, "出厂内置基线策略模板不能删除");
        }

        var attachedCount = await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM api_key WHERE PolicyTemplateIds LIKE @Pattern",
            new { Pattern = $"%{id}%" });
        if (attachedCount > 0)
        {
            return (false, $"该策略模板正被 {attachedCount} 个 API Key 引用，请先解除绑定后再行删除");
        }

        var affected = await db.ExecuteAsync("DELETE FROM security_policy_template WHERE Id = @Id COLLATE NOCASE", new { Id = id });
        return (affected > 0, affected > 0 ? null : "删除失败");
    }

    public async Task<IReadOnlyList<SecurityPolicyRule>> GetEffectiveRulesForApiKeyAsync(string apiKeyId)
    {
        if (string.IsNullOrWhiteSpace(apiKeyId)) return Array.Empty<SecurityPolicyRule>();

        using var db = factory.CreateConnection();
        var rawTemplateIds = await db.QueryFirstOrDefaultAsync<string>(
            "SELECT PolicyTemplateIds FROM api_key WHERE Id = @Id COLLATE NOCASE",
            new { Id = apiKeyId });

        var templateIds = ApiKeyService.ParsePolicyTemplateIds(rawTemplateIds);
        if (templateIds == null || templateIds.Count == 0)
        {
            return Array.Empty<SecurityPolicyRule>();
        }

        var templates = (await db.QueryAsync<SecurityPolicyTemplateEntity>(
            "SELECT Id, Name, RulesJson FROM security_policy_template WHERE Id IN @Ids",
            new { Ids = templateIds })).ToList();

        var result = new List<SecurityPolicyRule>();
        foreach (var tpl in templates)
        {
            if (string.IsNullOrWhiteSpace(tpl.RulesJson)) continue;
            try
            {
                var rules = JsonSerializer.Deserialize(tpl.RulesJson, SecurityJsonContext.Default.ListSecurityPolicyRule);
                if (rules != null)
                {
                    result.AddRange(rules);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "解析安全策略模板 '{Id}' 规则失败", tpl.Id);
            }
        }

        return result;
    }
}
