namespace LinuxWebTool.Contracts.Features.Mcp.Contracts;

/// <summary>MCP JSON-RPC 2.0 请求模型</summary>
public sealed record McpRpcRequest(
    string JsonRpc,
    string? Id,
    string Method,
    System.Text.Json.JsonElement? Params);

/// <summary>MCP JSON-RPC 2.0 响应模型</summary>
public sealed record McpRpcResponse(
    string JsonRpc,
    string? Id,
    System.Text.Json.JsonElement? Result,
    McpRpcError? Error);

/// <summary>MCP JSON-RPC 2.0 错误模型</summary>
public sealed record McpRpcError(
    int Code,
    string Message,
    object? Data = null);

/// <summary>MCP 工具定义模型</summary>
public sealed record McpToolDefinition(
    string Name,
    string Description,
    System.Text.Json.JsonElement InputSchema);

/// <summary>MCP 工具列表响应模型</summary>
public sealed record McpToolsListResult(
    List<McpToolDefinition> Tools);

/// <summary>MCP 工具返回内容项</summary>
public sealed record McpToolContent(
    string Type, // "text"
    string Text);

/// <summary>MCP 调用工具结果封装</summary>
public sealed record McpCallToolResult(
    List<McpToolContent> Content,
    bool IsError = false);

/// <summary>MCP 工具目录叶子项（二级细粒度功能）</summary>
public sealed record McpToolCatalogItem(
    string Name,
    string Description,
    string RiskLevel, // "Read", "Execute", "Dangerous"
    string ModuleName);

/// <summary>MCP 领域分类目录项（一级领域模块）</summary>
public sealed record McpCategoryCatalog(
    string Key,
    string Name,
    string Description,
    List<McpToolCatalogItem> Tools,
    string ModuleCode = "",
    bool SupportsApi = true,
    bool SupportsMcp = true);
