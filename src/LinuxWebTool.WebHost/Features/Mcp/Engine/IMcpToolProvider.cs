using System.Text.Json;
using LinuxWebTool.Contracts.Features.Mcp.Contracts;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;

namespace LinuxWebTool.WebHost.Features.Mcp.Engine;

/// <summary>
/// 业务领域 MCP 工具提供者抽象接口（策略模式 + 提供者模式）
/// 负责自包含管理所属模块的工具元数据、权限过滤与具体执行分发
/// </summary>
public interface IMcpToolProvider
{
    /// <summary>模块唯一标识（如 terminal, schedules, files, transcode, gateway, remote）</summary>
    string ModuleName { get; }

    /// <summary>当前调用者 API Key 是否具备访问该模块工具集的权限</summary>
    bool IsPermitted(ApiKeyEntity? callerKey);

    /// <summary>获取该模块下对外暴露的 MCP 工具定义列表（动态投影）</summary>
    IReadOnlyList<McpToolDefinition> GetTools(ApiKeyEntity? callerKey);

    /// <summary>分发并执行具体的工具调用；若该工具不属于本 Provider 则返回 null</summary>
    ValueTask<McpToolExecutionResult?> TryExecuteAsync(
        string toolName,
        JsonElement? arguments,
        McpRequestContext context);
}
