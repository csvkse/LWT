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
        if (toolName.StartsWith("terminal_", StringComparison.OrdinalIgnoreCase) ||
            toolName.StartsWith("command_", StringComparison.OrdinalIgnoreCase))
            return allowTerminal;
        if (toolName.StartsWith("schedule_", StringComparison.OrdinalIgnoreCase) ||
            toolName.StartsWith("workflow_", StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    /// 解析允许的 MCP 工具名集合（null 或包含 "*" 表示全部允许）
    /// </summary>
    public static HashSet<string>? ParseAllowedTools(string? allowedTools)
    {
        if (string.IsNullOrWhiteSpace(allowedTools) || allowedTools.Trim() == "*")
        {
            return null;
        }

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tokens = allowedTools.Split(new[] { ',', ';', ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            var cleaned = token.Trim().Trim('"', '[', ']', '\'', ',');
            if (!string.IsNullOrEmpty(cleaned))
            {
                set.Add(cleaned);
            }
        }
        return set;
    }

    /// <summary>
    /// 校验指定工具名称是否被允许（支持集合查找或原始串，null 或 "*" 表示全部允许）
    /// </summary>
    public static bool IsToolAllowed(string? allowedTools, string toolName)
    {
        if (string.IsNullOrWhiteSpace(allowedTools) || allowedTools.Trim() == "*")
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(toolName)) return false;

        var set = ParseAllowedTools(allowedTools);
        return set == null || set.Contains(toolName.Trim());
    }

    /// <summary>
    /// 校验指定工具名称是否在解析后的工具集合中
    /// </summary>
    public static bool IsToolAllowed(IReadOnlySet<string>? allowedToolSet, string toolName)
    {
        if (allowedToolSet == null || allowedToolSet.Count == 0 || allowedToolSet.Contains("*"))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(toolName)) return false;

        return allowedToolSet.Contains(toolName.Trim());
    }

    /// <summary>
    /// 二级细粒度工具权限判定（Level 1 模块开关 + Level 2 具体工具勾选）
    /// </summary>
    public static bool IsToolPermitted(
        bool allowTerminal,
        bool allowSchedules,
        bool allowFiles,
        bool allowTranscode,
        bool allowGateway,
        bool allowRemoteHosts,
        string? allowedTools,
        string toolName,
        string moduleName)
    {
        var moduleAllowed = moduleName.ToLowerInvariant() switch
        {
            "commands" or "terminal" => allowTerminal,
            "workflows" or "schedules" => allowSchedules,
            "files" or "file" => allowFiles,
            "transcode" => allowTranscode,
            "gateway" => allowGateway,
            "remote_hosts" or "remote" or "remotehosts" => allowRemoteHosts,
            _ => true
        };

        if (!moduleAllowed) return false;

        return IsToolAllowed(allowedTools, toolName);
    }
}
