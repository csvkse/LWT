using System.Text.Json;

namespace LinuxWebTool.Application.Features.Security;

/// <summary>
/// API Key 细粒度权限矩阵决断器（纯业务领域规则，AOT 兼容）：
/// - 统一控制 API 与 MCP 渠道访问边界；
/// - 提供各业务子模块（终端、调度、文件、转码、网关、远程主机）的执行授权断言；
/// - 支持 MCP 工具动态投影过滤与自定义主机白名单。
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
        return CanExecuteModule(allowTerminal, allowSchedules, allowFiles, allowTranscode, allowGateway, false, moduleName);
    }

    public static bool CanExecuteModule(
        bool allowTerminal,
        bool allowSchedules,
        bool allowFiles,
        bool allowTranscode,
        bool allowGateway,
        bool allowRemoteHosts,
        string moduleName)
    {
        return moduleName.ToLowerInvariant() switch
        {
            "terminal" => allowTerminal,
            "schedules" or "schedule" => allowSchedules,
            "files" or "file" => allowFiles,
            "transcode" => allowTranscode,
            "gateway" => allowGateway,
            "remotehosts" or "remotehost" or "remote" => allowRemoteHosts,
            _ => false
        };
    }

    /// <summary>
    /// 校验当前 API Key 是否被授权访问特定的远程主机 ID
    /// </summary>
    public static bool CanAccessRemoteHost(bool allowRemoteHosts, string? allowedHostIds, string targetHostId)
    {
        if (!allowRemoteHosts) return false;
        if (string.IsNullOrWhiteSpace(targetHostId)) return false;

        // 若未限制特定主机或为通配符 "*"，允许全部
        if (string.IsNullOrWhiteSpace(allowedHostIds) || allowedHostIds.Trim() == "*")
        {
            return true;
        }

        var trimmed = allowedHostIds.Trim();

        // 尝试以逗号/分号切分（兼容纯文本模式）
        var rawTokens = trimmed.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in rawTokens)
        {
            var cleaned = token.Trim().Trim('"', '[', ']', '\'', ' ');
            if (string.Equals(cleaned, targetHostId.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
        bool allowGateway,
        bool allowRemoteHosts = false)
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
        if (toolName.StartsWith("remote_", StringComparison.OrdinalIgnoreCase))
            return allowRemoteHosts;

        // 通用健康探测与基础信息工具默认允许
        return true;
    }
}
