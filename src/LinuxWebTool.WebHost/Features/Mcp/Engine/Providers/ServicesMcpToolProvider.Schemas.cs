using System.Text.Json;

namespace LinuxWebTool.WebHost.Features.Mcp.Engine.Providers;

public sealed partial class ServicesMcpToolProvider
{
    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static readonly JsonElement ServicesListSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "category": { "type": "string", "description": "筛选类别：all (全部) | systemd | docker", "enum": ["all", "systemd", "docker"] }
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
}
