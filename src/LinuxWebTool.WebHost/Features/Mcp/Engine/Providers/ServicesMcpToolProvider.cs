using System.Text;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.Mcp.Contracts;
using LinuxWebTool.Contracts.Features.Services.Contracts;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using LinuxWebTool.Infrastructure.Shared.Support;

namespace LinuxWebTool.WebHost.Features.Mcp.Engine.Providers;

/// <summary>
/// 宿主系统运维中枢 (Services Hub) MCP 工具提供者。
/// 专职负责 Linux 基础设施：Systemd 服务与 Docker 容器引擎。
/// </summary>
public sealed partial class ServicesMcpToolProvider(
    ISystemdServiceAdapter systemdAdapter,
    IDockerEngineAdapter dockerAdapter,
    IOperationLogger operationLogger) : IMcpToolProvider
{
    public string ModuleName => "services";

    public bool IsPermitted(ApiKeyEntity? callerKey) => callerKey == null || callerKey.AllowServices;

    public IReadOnlyList<McpToolDefinition> GetTools(ApiKeyEntity? callerKey)
    {
        return
        [
            new McpToolDefinition("services_list", "查询宿主 Systemd 服务及 Docker 容器运行状态", ServicesListSchema),
            new McpToolDefinition("systemd_action", "执行 Systemd 服务启停、重载与状态查询", SystemdActionSchema),
            new McpToolDefinition("docker_action", "执行 Docker 容器生命周期启停、重启或暂停", DockerActionSchema)
        ];
    }

    public async ValueTask<McpToolExecutionResult?> TryExecuteAsync(
        string toolName,
        JsonElement? arguments,
        McpRequestContext context)
    {
        var args = arguments ?? default;

        switch (toolName)
        {
            case "services_list":
                return await HandleServicesListAsync(args);

            case "systemd_action":
                return await HandleSystemdActionAsync(args, context);

            case "docker_action":
                return await HandleDockerActionAsync(args, context);

            default:
                return null;
        }
    }

    private async Task<McpToolExecutionResult> HandleServicesListAsync(JsonElement args)
    {
        var category = args.TryGetProperty("category", out var cProp) ? cProp.GetString() ?? "all" : "all";
        var sb = new StringBuilder();

        if (category is "all" or "systemd")
        {
            try
            {
                var units = await systemdAdapter.ListUnitsAsync(new SystemdQueryCriteria());
                sb.AppendLine($"=== Systemd 核心服务 ({units.Count} 个) ===");
                foreach (var u in units.Take(25))
                {
                    sb.AppendLine($"- {u.Unit}: {u.ActiveState}/{u.SubState} ({u.Description})");
                }
                if (units.Count > 25) sb.AppendLine($"  ... 及其余 {units.Count - 25} 个单元");
                sb.AppendLine();
            }
            catch (Exception ex)
            {
                sb.AppendLine($"=== Systemd: 读取不可用 ({ex.Message}) ===");
            }
        }

        if (category is "all" or "docker")
        {
            try
            {
                var containers = await dockerAdapter.ListContainersAsync(all: true);
                sb.AppendLine($"=== Docker 容器 ({containers.Count} 个) ===");
                foreach (var c in containers)
                {
                    var name = c.Names.FirstOrDefault() ?? c.Id;
                    sb.AppendLine($"- [{c.Id[..Math.Min(12, c.Id.Length)]}] {name} ({c.Image}): {c.State} - {c.Status}");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"=== Docker: 引擎未运行或无权限 ({ex.Message}) ===");
            }
        }

        return McpToolExecutionResult.Success(sb.ToString().TrimEnd());
    }

    private async Task<McpToolExecutionResult> HandleSystemdActionAsync(JsonElement args, McpRequestContext context)
    {
        if (!args.TryGetProperty("service_name", out var sProp) || string.IsNullOrWhiteSpace(sProp.GetString()))
            return McpToolExecutionResult.Error("缺少 service_name 参数");
        if (!args.TryGetProperty("action", out var aProp) || string.IsNullOrWhiteSpace(aProp.GetString()))
            return McpToolExecutionResult.Error("缺少 action 参数");

        var serviceName = sProp.GetString()!.Trim();
        var actionStr = aProp.GetString()!.Trim().ToLowerInvariant();

        var actionType = actionStr switch
        {
            "start" => ServiceActionType.Start,
            "stop" => ServiceActionType.Stop,
            "restart" => ServiceActionType.Restart,
            "reload" => ServiceActionType.Reload,
            "enable" => ServiceActionType.Enable,
            "disable" => ServiceActionType.Disable,
            _ => ServiceActionType.Restart
        };

        var res = await systemdAdapter.ExecuteActionAsync(serviceName, actionType);
        await operationLogger.LogAsync(
            "MCP Systemd控制", "服务运维", serviceName,
            $"动作: {actionStr}, 结果: {res.Success}, 消息: {res.Message}",
            success: res.Success, clientIp: context.ClientIp);

        return res.Success ? McpToolExecutionResult.Success(res.Message ?? "操作成功") : McpToolExecutionResult.Error(res.Message ?? "操作失败");
    }

    private async Task<McpToolExecutionResult> HandleDockerActionAsync(JsonElement args, McpRequestContext context)
    {
        if (!args.TryGetProperty("container_id", out var cProp) || string.IsNullOrWhiteSpace(cProp.GetString()))
            return McpToolExecutionResult.Error("缺少 container_id 参数");
        if (!args.TryGetProperty("action", out var aProp) || string.IsNullOrWhiteSpace(aProp.GetString()))
            return McpToolExecutionResult.Error("缺少 action 参数");

        var containerId = cProp.GetString()!.Trim();
        var actionStr = aProp.GetString()!.Trim().ToLowerInvariant();

        var action = actionStr switch
        {
            "start" => DockerContainerAction.Start,
            "stop" => DockerContainerAction.Stop,
            "restart" => DockerContainerAction.Restart,
            "pause" => DockerContainerAction.Pause,
            "unpause" => DockerContainerAction.Unpause,
            "kill" => DockerContainerAction.Kill,
            "remove" => DockerContainerAction.Remove,
            _ => DockerContainerAction.Restart
        };

        var res = await dockerAdapter.ExecuteContainerActionAsync(new DockerContainerActionRequest
        {
            ContainerId = containerId,
            Action = action
        });

        await operationLogger.LogAsync(
            "MCP Docker控制", "服务运维", containerId,
            $"动作: {actionStr}, 结果: {res.Success}, 消息: {res.Message}",
            success: res.Success, clientIp: context.ClientIp);

        return res.Success ? McpToolExecutionResult.Success(res.Message ?? "操作成功") : McpToolExecutionResult.Error(res.Message ?? "操作失败");
    }
}
