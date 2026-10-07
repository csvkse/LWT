
namespace LinuxWebTool.Application.Features.Security;

/// <summary>
/// API Key 细粒度权限矩阵决断器（纯业务领域规则，AOT 兼容）：
/// - 统一控制 API 与 MCP 渠道访问边界；
/// - 提供各业务子模块（终端、调度、文件、转码、网关）的执行授权断言；
/// - 支持 MCP 工具动态投影过滤。
/// </summary>
public static class ApiKeyPermissionMatrix
{
    public static bool CanAccessChannel(bool allowApi, bool allowMcp, string channel)
    {
        return channel.ToLowerInvariant() switch
        {
            "api" => allowApi,
            "mcp" => allowMcp,
            _ => false
        };
    }

    public static bool CanExecuteModule(
        bool allowTerminal,
        bool allowSchedules,
        bool allowFiles,
        bool allowTranscode,
        bool allowGateway,
        string moduleName)
    {
        return moduleName.ToLowerInvariant() switch
        {
            "terminal" => allowTerminal,
            "schedules" or "schedule" => allowSchedules,
            "files" or "file" => allowFiles,
            "transcode" => allowTranscode,
            "gateway" => allowGateway,
            _ => false
        };
    }

    /// <summary>
    /// 依据权限标记对 MCP 工具列表进行动态过滤投影
    /// </summary>
    public static bool IsMcpToolPermitted(
        string toolName,
        bool allowTerminal,
        bool allowSchedules,
        bool allowFiles,
        bool allowTranscode,
        bool allowGateway)
    {
        if (toolName.StartsWith("terminal_", StringComparison.OrdinalIgnoreCase))
            return allowTerminal;
        if (toolName.StartsWith("schedule_", StringComparison.OrdinalIgnoreCase))
            return allowSchedules;
        if (toolName.StartsWith("file_", StringComparison.OrdinalIgnoreCase))
            return allowFiles;
        if (toolName.StartsWith("transcode_", StringComparison.OrdinalIgnoreCase))
            return allowTranscode;
        if (toolName.StartsWith("gateway_", StringComparison.OrdinalIgnoreCase))
            return allowGateway;

        // 通用健康探测与基础信息工具默认允许
        return true;
    }
}
