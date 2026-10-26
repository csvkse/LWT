using System.Text;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.Mcp.Contracts;
using LinuxWebTool.Infrastructure.Shared.Persistence;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;

namespace LinuxWebTool.WebHost.Features.Mcp.Engine.Providers;

/// <summary>
/// 反向代理网关与网站路由 MCP 工具提供者
/// </summary>
public sealed class GatewayMcpToolProvider(
    GatewayStore gatewayStore) : IMcpToolProvider
{
    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static readonly JsonElement EmptyObjectSchema = Schema("""{"type":"object","properties":{}}""");

    public string ModuleName => "gateway";

    public bool IsPermitted(ApiKeyEntity? callerKey) => callerKey == null || callerKey.AllowGateway;

    public IReadOnlyList<McpToolDefinition> GetTools(ApiKeyEntity? callerKey)
    {
        return
        [
            new McpToolDefinition(
                "gateway_list_routes",
                "查询家庭网关中已配置的 L7 HTTP 反向代理路由与网站代理",
                EmptyObjectSchema)
        ];
    }

    public async ValueTask<McpToolExecutionResult?> TryExecuteAsync(
        string toolName,
        JsonElement? arguments,
        McpRequestContext context)
    {
        switch (toolName)
        {
            case "gateway_list_routes":
            {
                var routes = await gatewayStore.GetAllRoutesAsync();
                var websites = await gatewayStore.GetAllWebsitesAsync();

                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(ms))
                {
                    writer.WriteStartObject();
                    writer.WriteStartArray("routes");
                    foreach (var r in routes)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("route_id", r.RouteId);
                        writer.WriteString("cluster_id", r.ClusterId);
                        writer.WriteString("match_path", r.MatchPath);
                        writer.WriteBoolean("enabled", r.IsEnabled);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteStartArray("websites");
                    foreach (var w in websites)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("name", w.Name);
                        writer.WriteString("target_url", w.TargetUrl);
                        writer.WriteBoolean("enabled", w.IsEnabled);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                return McpToolExecutionResult.Success(Encoding.UTF8.GetString(ms.ToArray()));
            }

            default:
                return null;
        }
    }
}
