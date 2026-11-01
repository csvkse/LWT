using System.Text.Json;

namespace LinuxWebTool.WebHost.Features.Mcp.Engine.Providers;

public sealed partial class PluginsMcpToolProvider
{
    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static readonly JsonElement EmptyObjectSchema = Schema("""{ "type": "object" }""");

    private static readonly JsonElement PluginControlSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "plugin_id": { "type": "string", "description": "插件唯一 ID" },
            "action": { "type": "string", "description": "控制动作：start (启动) | stop (停止) | restart (重启)", "enum": ["start", "stop", "restart"] }
        },
        "required": ["plugin_id", "action"]
    }
    """);

    private static readonly JsonElement PluginInvokeSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "plugin_id": { "type": "string", "description": "微服务插件 ID" },
            "path": { "type": "string", "description": "请求路径 (如 /api/status 或 webhook)" },
            "method": { "type": "string", "description": "HTTP 请求方法 (GET, POST, PUT, DELETE)", "enum": ["GET", "POST", "PUT", "DELETE"] },
            "body": { "type": "string", "description": "请求体 JSON 字符串（仅适用于 POST/PUT 等）" }
        },
        "required": ["plugin_id"]
    }
    """);

    private static readonly JsonElement PluginActionSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "plugin_id": { "type": "string", "description": "插件 ID" },
            "action": { "type": "string", "description": "自定义动作名称" },
            "param": { "type": "string", "description": "动作参数值（可选）" }
        },
        "required": ["plugin_id", "action"]
    }
    """);

    private static readonly JsonElement PluginGetLogsSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "plugin_id": { "type": "string", "description": "插件唯一 ID" },
            "limit": { "type": "integer", "description": "返回最新日志行数（默认 200，最大 1000）" }
        },
        "required": ["plugin_id"]
    }
    """);

    private static readonly JsonElement PluginSetEnabledSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "plugin_id": { "type": "string", "description": "插件唯一 ID" },
            "enabled": { "type": "boolean", "description": "true 为启用，false 为禁用" }
        },
        "required": ["plugin_id", "enabled"]
    }
    """);

    private static readonly JsonElement PluginGetCodeSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "plugin_id": { "type": "string", "description": "插件唯一 ID" },
            "file": { "type": "string", "description": "相对文件路径（可选，为空则默认入口代码文件）" }
        },
        "required": ["plugin_id"]
    }
    """);

    private static readonly JsonElement PluginEditCodeSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "plugin_id": { "type": "string", "description": "插件唯一 ID" },
            "file": { "type": "string", "description": "相对文件路径（如 worker.py 或 plugin.json）" },
            "content": { "type": "string", "description": "修改后的完整文件内容" }
        },
        "required": ["plugin_id", "content"]
    }
    """);

    private static readonly JsonElement PluginDebugRunSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "plugin_id": { "type": "string", "description": "插件唯一 ID" },
            "action": { "type": "string", "description": "调试动作名称" },
            "param": { "type": "string", "description": "参数值" }
        },
        "required": ["plugin_id"]
    }
    """);
}
