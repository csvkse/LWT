using System.Collections.Frozen;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.Mcp.Contracts;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using LinuxWebTool.WebHost.Composition;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.WebHost.Features.Mcp.Engine;

/// <summary>
/// 纯原生 Native AOT 兼容的 MCP (Model Context Protocol) 核心分发引擎（Facade & Dispatcher）
/// 基于领域策略提供者模式（IMcpToolProvider）解耦，支持 O(1) 路由分发、动态权限投影与全局安全沙箱
/// </summary>
public sealed class McpServerEngine
{
    private static readonly JsonElement EmptyElement = JsonDocument.Parse("{}").RootElement.Clone();
    private static readonly JsonElement InitializeElement = JsonDocument.Parse("""
    {
        "protocolVersion": "2024-11-05",
        "capabilities": {
            "tools": {}
        },
        "serverInfo": {
            "name": "LinuxWebTool-MCP-Server",
            "version": "2.0.0"
        }
    }
    """).RootElement.Clone();

    private readonly IReadOnlyList<IMcpToolProvider> _providers;
    private readonly FrozenDictionary<string, IMcpToolProvider> _toolToProviderMap;
    private readonly ILogger<McpServerEngine> _logger;

    public McpServerEngine(
        IEnumerable<IMcpToolProvider> providers,
        ILogger<McpServerEngine> logger)
    {
        _providers = providers.ToList();
        _logger = logger;

        // 构建 O(1) 快速工具分发索引字典
        var dict = new Dictionary<string, IMcpToolProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _providers)
        {
            var tools = p.GetTools(null);
            foreach (var t in tools)
            {
                dict[t.Name] = p;
            }
        }
        _toolToProviderMap = dict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<McpCategoryCatalog> GetCatalog() => McpCatalogRegistry.GetCatalog();

    public async Task<McpRpcResponse> HandleRequestAsync(McpRpcRequest request, ApiKeyEntity? callerKey, HttpContext httpContext)
    {
        try
        {
            return request.Method switch
            {
                "initialize" => new McpRpcResponse("2.0", request.Id, InitializeElement, null),
                "notifications/initialized" => new McpRpcResponse("2.0", request.Id, EmptyElement, null),
                "ping" => new McpRpcResponse("2.0", request.Id, EmptyElement, null),
                "tools/list" => HandleToolsList(request, callerKey),
                "tools/call" => await HandleToolCallAsync(request, callerKey, httpContext),
                _ => new McpRpcResponse("2.0", request.Id, null, new McpRpcError(-32601, $"Method not found: {request.Method}"))
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理 MCP 请求异常: {Method}", request.Method);
            return new McpRpcResponse("2.0", request.Id, null, new McpRpcError(-32603, $"Internal error: {ex.Message}"));
        }
    }

    private McpRpcResponse HandleToolsList(McpRpcRequest request, ApiKeyEntity? callerKey)
    {
        var tools = new List<McpToolDefinition>();
        var allowedSet = callerKey != null ? Application.Features.Security.ApiKeyPermissionMatrix.ParseAllowedTools(callerKey.AllowedTools) : null;

        foreach (var provider in _providers)
        {
            if (provider.IsPermitted(callerKey))
            {
                var providerTools = provider.GetTools(callerKey);
                foreach (var tool in providerTools)
                {
                    if (allowedSet == null || allowedSet.Contains(tool.Name))
                    {
                        tools.Add(tool);
                    }
                }
            }
        }

        var element = JsonSerializer.SerializeToElement(new McpToolsListResult(tools), AppJsonSerializerContext.Default.McpToolsListResult);
        return new McpRpcResponse("2.0", request.Id, element, null);
    }

    private async Task<McpRpcResponse> HandleToolCallAsync(McpRpcRequest request, ApiKeyEntity? callerKey, HttpContext httpContext)
    {
        if (!request.Params.HasValue)
        {
            return new McpRpcResponse("2.0", request.Id, null, new McpRpcError(-32602, "Missing params"));
        }

        var root = request.Params.Value;
        if (!root.TryGetProperty("name", out var nameProp))
        {
            return new McpRpcResponse("2.0", request.Id, null, new McpRpcError(-32602, "Missing tool name"));
        }

        var toolName = nameProp.GetString() ?? string.Empty;
        var args = root.TryGetProperty("arguments", out var argsProp) ? argsProp : default;

        // O(1) 定位目标工具提供者
        if (!_toolToProviderMap.TryGetValue(toolName, out var provider))
        {
            return new McpRpcResponse("2.0", request.Id, null, new McpRpcError(-32601, $"Unknown tool: {toolName}"));
        }

        // 模块级双重安全防护拦截
        if (!provider.IsPermitted(callerKey))
        {
            return ToolErrorResponse(request.Id, $"Permission Denied: API Key does not have permission for module '{provider.ModuleName}'.");
        }

        // 二级细粒度工具权限独立拦截
        if (callerKey != null && !Application.Features.Security.ApiKeyPermissionMatrix.IsToolAllowed(callerKey.AllowedTools, toolName))
        {
            return ToolErrorResponse(request.Id, $"Permission Denied: API Key is not authorized to execute tool '{toolName}'.");
        }

        var clientIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var context = new McpRequestContext(callerKey, httpContext, clientIp, httpContext.RequestAborted);

        try
        {
            var result = await provider.TryExecuteAsync(toolName, args, context);
            if (result == null)
            {
                return new McpRpcResponse("2.0", request.Id, null, new McpRpcError(-32601, $"Tool handler returned null: {toolName}"));
            }

            return result.IsError
                ? ToolErrorResponse(request.Id, result.Content)
                : ToolSuccessResponse(request.Id, result.Content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "执行 MCP 工具异常: {ToolName}", toolName);
            return ToolErrorResponse(request.Id, $"Tool execution failed: {ex.Message}");
        }
    }

    private static McpRpcResponse ToolSuccessResponse(string? id, string text)
    {
        var result = new McpCallToolResult([new McpToolContent("text", text)], false);
        var element = JsonSerializer.SerializeToElement(result, AppJsonSerializerContext.Default.McpCallToolResult);
        return new McpRpcResponse("2.0", id, element, null);
    }

    private static McpRpcResponse ToolErrorResponse(string? id, string errorMessage)
    {
        var result = new McpCallToolResult([new McpToolContent("text", errorMessage)], true);
        var element = JsonSerializer.SerializeToElement(result, AppJsonSerializerContext.Default.McpCallToolResult);
        return new McpRpcResponse("2.0", id, element, null);
    }
}
