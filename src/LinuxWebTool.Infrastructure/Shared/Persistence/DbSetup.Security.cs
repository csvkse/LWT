using System.Text.Json;
using LinuxWebTool.Contracts.Features.Security.Contracts;
using LinuxWebTool.Infrastructure.Features.Security.Policies;
using Microsoft.Data.Sqlite;

namespace LinuxWebTool.Infrastructure.Shared.Persistence;

public static partial class DbSetup
{
    private static void MigrateAndSeedSecurityPolicies(SqliteConnection db)
    {
        var now = System.DateTime.UtcNow.ToString("O");
        const string dockerRules = """[{"RuleId":"r_docker_allow","Name":"Docker 常用运维动词白名单","Domain":3,"Effect":1,"CommandPatterns":"docker ps, docker logs, docker stats, docker restart, docker top, docker compose ps, docker compose logs","PathPatterns":null,"DisallowSubshells":true,"DisallowCommandChaining":true,"BlockHighRiskFlags":true,"ReadOnlyOnly":false}]""";
        const string workspaceRules = """[{"RuleId":"r_workspace_allow","Name":"专属工作区目录限定","Domain":12,"Effect":1,"CommandPatterns":null,"PathPatterns":"/home;/data/projects","DisallowSubshells":false,"DisallowCommandChaining":false,"BlockHighRiskFlags":false,"ReadOnlyOnly":false}]""";
        const string readonlyRules = """[{"RuleId":"r_readonly_fs","Name":"文件系统强制只读","Domain":12,"Effect":1,"CommandPatterns":null,"PathPatterns":"*","DisallowSubshells":false,"DisallowCommandChaining":false,"BlockHighRiskFlags":false,"ReadOnlyOnly":true},{"RuleId":"r_readonly_cmd","Name":"只读状态探针指令","Domain":3,"Effect":1,"CommandPatterns":"uptime, df, free, uname, top -b -n 1, ps aux, systemctl status *","PathPatterns":null,"DisallowSubshells":true,"DisallowCommandChaining":true,"BlockHighRiskFlags":true,"ReadOnlyOnly":false}]""";

        UpsertBuiltinTemplate(db, "tpl_docker_ops", "🐳 Docker 容器运维白名单 (内置)", "仅允许常用的容器查看、监控与安全重启操作，拦截特权模式与根目录挂载", dockerRules, now);
        UpsertBuiltinTemplate(db, "tpl_workspace_only", "📁 专属工作区目录限制 (内置)", "严格限定文件访问与工作目录在 /home 与 /data/projects，阻断全盘穿透", workspaceRules, now);
        UpsertBuiltinTemplate(db, "tpl_readonly_safe", "🔒 生产环境只读防护 (内置)", "严格禁止文件写入、删除与覆盖，终端仅放行无副作用的探针巡检指令", readonlyRules, now);

        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT Id, Name, AllowedPaths, PolicyTemplateIds FROM api_key WHERE AllowedPaths IS NOT NULL AND trim(AllowedPaths) != ''";
            using var reader = cmd.ExecuteReader();
            var toMigrate = new System.Collections.Generic.List<(string Id, string Name, string Paths)>();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var name = reader.GetString(1);
                var paths = reader.GetString(2);
                var pIds = reader.IsDBNull(3) ? null : reader.GetString(3);
                if (string.IsNullOrWhiteSpace(pIds))
                {
                    toMigrate.Add((id, name, paths));
                }
            }
            reader.Close();

            foreach (var item in toMigrate)
            {
                var tplId = $"tpl_migrated_{item.Id[..Math.Min(8, item.Id.Length)]}";
                var rulesJson = JsonSerializer.Serialize(new List<SecurityPolicyRule>
                {
                    new(
                        RuleId: "r_migrated_path",
                        Name: $"{item.Name} 原有路径防护",
                        Domain: SecurityTargetDomain.LocalFile | SecurityTargetDomain.RemoteFile,
                        Effect: PolicyEffect.Allow,
                        CommandPatterns: null,
                        PathPatterns: item.Paths,
                        DisallowSubshells: false,
                        DisallowCommandChaining: false,
                        BlockHighRiskFlags: false,
                        ReadOnlyOnly: false
                    )
                }, SecurityJsonContext.Default.ListSecurityPolicyRule);

                using (var insertCmd = db.CreateCommand())
                {
                    insertCmd.CommandText = @"
                        INSERT OR IGNORE INTO security_policy_template (Id, Name, Description, IsBuiltin, RulesJson, CreatedAt, UpdatedAt)
                        VALUES (@Id, @Name, @Desc, 0, @Rules, @Now, @Now)";
                    insertCmd.Parameters.AddWithValue("@Id", tplId);
                    insertCmd.Parameters.AddWithValue("@Name", $"{item.Name}-路径策略");
                    insertCmd.Parameters.AddWithValue("@Desc", "系统自动合并迁移生成的路径策略");
                    insertCmd.Parameters.AddWithValue("@Rules", rulesJson);
                    insertCmd.Parameters.AddWithValue("@Now", now);
                    insertCmd.ExecuteNonQuery();
                }

                using (var updateCmd = db.CreateCommand())
                {
                    updateCmd.CommandText = "UPDATE api_key SET PolicyTemplateIds = @Pids, AllowedPaths = NULL WHERE Id = @Id";
                    updateCmd.Parameters.AddWithValue("@Pids", $"[\"{tplId}\"]");
                    updateCmd.Parameters.AddWithValue("@Id", item.Id);
                    updateCmd.ExecuteNonQuery();
                }
            }
        }
        catch { }
    }

    private static void UpsertBuiltinTemplate(SqliteConnection db, string id, string name, string desc, string rulesJson, string now)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO security_policy_template (Id, Name, Description, IsBuiltin, RulesJson, CreatedAt, UpdatedAt)
            VALUES (@Id, @Name, @Desc, 1, @RulesJson, @Now, @Now)
            ON CONFLICT(Id) DO UPDATE SET Name = @Name, Description = @Desc, RulesJson = @RulesJson, UpdatedAt = @Now";
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@Name", name);
        cmd.Parameters.AddWithValue("@Desc", desc);
        cmd.Parameters.AddWithValue("@RulesJson", rulesJson);
        cmd.Parameters.AddWithValue("@Now", now);
        cmd.ExecuteNonQuery();
    }
}
