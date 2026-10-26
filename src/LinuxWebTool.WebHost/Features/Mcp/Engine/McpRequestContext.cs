using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using Microsoft.AspNetCore.Http;

namespace LinuxWebTool.WebHost.Features.Mcp.Engine;

/// <summary>
/// MCP 请求处理上下文，聚合客户端身份凭据、HTTP 上下文与取消令牌
/// </summary>
public sealed record McpRequestContext(
    ApiKeyEntity? CallerKey,
    HttpContext HttpContext,
    string ClientIp,
    CancellationToken CancellationToken);
