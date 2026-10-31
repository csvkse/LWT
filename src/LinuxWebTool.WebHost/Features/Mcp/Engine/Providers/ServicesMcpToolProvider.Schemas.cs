using System.Text.Json;

namespace LinuxWebTool.WebHost.Features.Mcp.Engine.Providers;

public sealed partial class ServicesMcpToolProvider
{
    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static readonly JsonElement ServicesListSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "category": { "type": "string", "description": "筛选类别：all (全部) | systemd | docker | extension", "enum": ["all", "systemd", "docker", "extension"] }
        }
    }
    """);

    private static readonly JsonElement SystemdActionSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "service_name": { "type": "string", "description": "Systemd 服务单元名称，如 sshd.service, nginx.service" },
            "action": { "type": "string", "description": "执行动作：start | stop | restart | reload | enable | disable", "enum": ["start", "stop", "restart", "reload", "enable", "disable"] }
        },
        "required": ["service_name", "action"]
    }
    """);

    private static readonly JsonElement DockerActionSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "container_id": { "type": "string", "description": "Docker 容器名称或 ID" },
            "action": { "type": "string", "description": "执行动作：start | stop | restart | pause | unpause | kill | remove", "enum": ["start", "stop", "restart", "pause", "unpause", "kill", "remove"] }
        },
        "required": ["container_id", "action"]
    }
    """);

    private static readonly JsonElement ExtensionActionSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "extension_id": { "type": "string", "description": "扩展服务唯一标识 ID" },
            "action": { "type": "string", "description": "自定义动作名称，如 query_banned, ban_ip, unban_ip, top_snapshot" },
            "params": {
                "type": "object",
                "description": "动作参数字典键值对（可选）",
                "additionalProperties": { "type": "string" }
            }
        },
        "required": ["extension_id", "action"]
    }
    """);

    private static readonly JsonElement ExtensionImportSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "repo_url": { "type": "string", "description": "Git 仓库克隆地址 (GitHub/Gitee 等)" },
            "module_ids": {
                "type": "array",
                "items": { "type": "string" },
                "description": "待导入的扩展模块 ID 列表"
            },
            "overwrite": { "type": "boolean", "description": "若已存在是否覆盖（默认 true）" }
        },
        "required": ["repo_url", "module_ids"]
    }
    """);

    private static readonly JsonElement ExtensionSetEnabledSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "extension_id": { "type": "string", "description": "扩展服务 ID" },
            "enabled": { "type": "boolean", "description": "true 为启用，false 为禁用" }
        },
        "required": ["extension_id", "enabled"]
    }
    """);

    private static readonly JsonElement ExtensionCheckUpdateSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "extension_id": { "type": "string", "description": "扩展服务 ID（可选，为空则检查全部）" }
        }
    }
    """);

    private static readonly JsonElement ExtensionHotUpdateSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "extension_id": { "type": "string", "description": "扩展服务 ID" },
            "force": { "type": "boolean", "description": "是否强制覆盖（默认 false）" }
        },
        "required": ["extension_id"]
    }
    """);
}
