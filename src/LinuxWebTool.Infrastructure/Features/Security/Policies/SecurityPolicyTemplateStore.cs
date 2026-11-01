using System.Text.Json;
using LinuxWebTool.Contracts.Features.Security.Contracts;
using LinuxWebTool.Infrastructure.Shared.Persistence;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Security.Policies;

public sealed class SecurityPolicyTemplateStore(
    DbConnectionFactory factory,
    ILogger<SecurityPolicyTemplateStore> logger) : ISecurityPolicyTemplateStore
{
    private static DateTime ParseDate(string? s) =>
        DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt : DateTime.UtcNow;

    public async Task<IReadOnlyList<SecurityPolicyTemplateItemResponse>> GetAllAsync()
    {
        using var db = factory.CreateConnection();
        await db.OpenAsync();

        var entities = new List<SecurityPolicyTemplateEntity>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, Name, Description, IsBuiltin, RulesJson, CreatedAt, UpdatedAt FROM security_policy_template ORDER BY IsBuiltin DESC, CreatedAt ASC";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                entities.Add(new SecurityPolicyTemplateEntity
                {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                    IsBuiltin = reader.GetInt32(3) == 1,
                    RulesJson = reader.GetString(4),
                    CreatedAt = ParseDate(reader.GetString(5)),
                    UpdatedAt = ParseDate(reader.GetString(6))
                });
            }
        }

        var usageCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT PolicyTemplateIds FROM api_key WHERE PolicyTemplateIds IS NOT NULL AND PolicyTemplateIds != ''";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var raw = reader.GetString(0);
                var ids = ApiKeyService.ParsePolicyTemplateIds(raw);
                if (ids != null)
                {
                    foreach (var id in ids)
                    {
                        usageCounts[id] = usageCounts.GetValueOrDefault(id, 0) + 1;
                    }
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
        await db.OpenAsync();

        SecurityPolicyTemplateEntity? entity = null;
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, Name, Description, IsBuiltin, RulesJson, CreatedAt, UpdatedAt FROM security_policy_template WHERE Id = @Id COLLATE NOCASE";
            cmd.Parameters.AddWithValue("@Id", id);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                entity = new SecurityPolicyTemplateEntity
                {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                    IsBuiltin = reader.GetInt32(3) == 1,
                    RulesJson = reader.GetString(4),
                    CreatedAt = ParseDate(reader.GetString(5)),
                    UpdatedAt = ParseDate(reader.GetString(6))
                };
            }
        }

        if (entity is null) return null;

        var count = 0;
        using (var countCmd = db.CreateCommand())
        {
            countCmd.CommandText = "SELECT COUNT(1) FROM api_key WHERE PolicyTemplateIds LIKE @Pattern";
            countCmd.Parameters.AddWithValue("@Pattern", $"%{entity.Id}%");
            var scalar = await countCmd.ExecuteScalarAsync();
            if (scalar != null && scalar != DBNull.Value)
            {
                count = Convert.ToInt32(scalar);
            }
        }

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
        var now = DateTime.UtcNow.ToString("O");

        using var db = factory.CreateConnection();
        await db.OpenAsync();
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO security_policy_template (Id, Name, Description, IsBuiltin, RulesJson, CreatedAt, UpdatedAt)
            VALUES (@Id, @Name, @Description, 0, @RulesJson, @CreatedAt, @UpdatedAt)";

        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@Name", request.Name.Trim());
        cmd.Parameters.AddWithValue("@Description", (object?)request.Description?.Trim() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@RulesJson", string.IsNullOrWhiteSpace(request.RulesJson) ? "[]" : request.RulesJson.Trim());
        cmd.Parameters.AddWithValue("@CreatedAt", now);
        cmd.Parameters.AddWithValue("@UpdatedAt", now);

        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    public async Task<bool> UpdateAsync(string id, UpdateSecurityPolicyTemplateRequest request)
    {
        using var db = factory.CreateConnection();
        await db.OpenAsync();

        using (var checkCmd = db.CreateCommand())
        {
            checkCmd.CommandText = "SELECT Id FROM security_policy_template WHERE Id = @Id COLLATE NOCASE";
            checkCmd.Parameters.AddWithValue("@Id", id);
            var exists = await checkCmd.ExecuteScalarAsync();
            if (exists is null || exists == DBNull.Value) return false;
        }

        using var cmd = db.CreateCommand();
        cmd.CommandText = @"
            UPDATE security_policy_template
            SET Name = @Name, Description = @Description, RulesJson = @RulesJson, UpdatedAt = @UpdatedAt
            WHERE Id = @Id COLLATE NOCASE";

        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@Name", request.Name.Trim());
        cmd.Parameters.AddWithValue("@Description", (object?)request.Description?.Trim() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@RulesJson", string.IsNullOrWhiteSpace(request.RulesJson) ? "[]" : request.RulesJson.Trim());
        cmd.Parameters.AddWithValue("@UpdatedAt", DateTime.UtcNow.ToString("O"));

        var affected = await cmd.ExecuteNonQueryAsync();
        return affected > 0;
    }

    public async Task<(bool Success, string? Error)> DeleteAsync(string id)
    {
        using var db = factory.CreateConnection();
        await db.OpenAsync();

        bool isBuiltin = false;
        using (var checkCmd = db.CreateCommand())
        {
            checkCmd.CommandText = "SELECT IsBuiltin FROM security_policy_template WHERE Id = @Id COLLATE NOCASE";
            checkCmd.Parameters.AddWithValue("@Id", id);
            var scalar = await checkCmd.ExecuteScalarAsync();
            if (scalar is null || scalar == DBNull.Value) return (false, "策略模板不存在");
            isBuiltin = Convert.ToInt32(scalar) == 1;
        }

        if (isBuiltin)
        {
            return (false, "出厂内置基线策略模板不能删除");
        }

        var attachedCount = 0;
        using (var countCmd = db.CreateCommand())
        {
            countCmd.CommandText = "SELECT COUNT(1) FROM api_key WHERE PolicyTemplateIds LIKE @Pattern";
            countCmd.Parameters.AddWithValue("@Pattern", $"%{id}%");
            var countScalar = await countCmd.ExecuteScalarAsync();
            if (countScalar != null && countScalar != DBNull.Value)
            {
                attachedCount = Convert.ToInt32(countScalar);
            }
        }

        if (attachedCount > 0)
        {
            return (false, $"该策略模板正被 {attachedCount} 个 API Key 引用，请先解除绑定后再行删除");
        }

        using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM security_policy_template WHERE Id = @Id COLLATE NOCASE";
        cmd.Parameters.AddWithValue("@Id", id);
        var affected = await cmd.ExecuteNonQueryAsync();
        return (affected > 0, affected > 0 ? null : "删除失败");
    }

    public async Task<IReadOnlyList<SecurityPolicyRule>> GetEffectiveRulesForApiKeyAsync(string apiKeyId)
    {
        if (string.IsNullOrWhiteSpace(apiKeyId)) return Array.Empty<SecurityPolicyRule>();

        using var db = factory.CreateConnection();
        await db.OpenAsync();

        string? rawTemplateIds = null;
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT PolicyTemplateIds FROM api_key WHERE Id = @Id COLLATE NOCASE";
            cmd.Parameters.AddWithValue("@Id", apiKeyId);
            var scalar = await cmd.ExecuteScalarAsync();
            if (scalar != null && scalar != DBNull.Value)
            {
                rawTemplateIds = scalar.ToString();
            }
        }

        var templateIds = ApiKeyService.ParsePolicyTemplateIds(rawTemplateIds);
        if (templateIds == null || templateIds.Count == 0)
        {
            return Array.Empty<SecurityPolicyRule>();
        }

        var templates = new List<(string Id, string Name, string RulesJson)>();
        using (var cmd = db.CreateCommand())
        {
            var paramNames = new List<string>();
            for (var i = 0; i < templateIds.Count; i++)
            {
                var p = $"@id{i}";
                paramNames.Add(p);
                cmd.Parameters.AddWithValue(p, templateIds[i]);
            }
            cmd.CommandText = $"SELECT Id, Name, RulesJson FROM security_policy_template WHERE Id IN ({string.Join(",", paramNames)})";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                templates.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

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
