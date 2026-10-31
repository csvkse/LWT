using System.Text;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.Mcp.Contracts;
using LinuxWebTool.Contracts.Features.Services.Contracts;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using LinuxWebTool.Infrastructure.Shared.Support;

namespace LinuxWebTool.WebHost.Features.Mcp.Engine.Providers;

/// <summary>
/// 宿主系统运维与扩展服务 (Services Hub) MCP 工具提供者
/// </summary>
public sealed partial class ServicesMcpToolProvider(
    ISystemdServiceAdapter systemdAdapter,
    IDockerEngineAdapter dockerAdapter,
    IScriptExtensionManager extensionManager,
    IOperationLogger operationLogger) : IMcpToolProvider
{
    public string ModuleName => "services";

    public bool IsPermitted(ApiKeyEntity? callerKey) => callerKey == null || callerKey.AllowServices;

    public IReadOnlyList<McpToolDefinition> GetTools(ApiKeyEntity? callerKey)
    {
        return
        [
            new McpToolDefinition("services_list", "查询宿主 Systemd 服务、Docker 容器及运维扩展状态与启用状态", ServicesListSchema),
            new McpToolDefinition("systemd_action", "执行 Systemd 服务启停、重载与状态查询", SystemdActionSchema),
            new McpToolDefinition("docker_action", "执行 Docker 容器生命周期启停、重启或暂停", DockerActionSchema),
            new McpToolDefinition("extension_execute_action", "执行指定运维扩展自定义钩子动作（如封禁IP、查Top监控等）", ExtensionActionSchema),
            new McpToolDefinition("extension_import", "从 Git 仓库批量导入一个或多个运维扩展服务", ExtensionImportSchema),
            new McpToolDefinition("extension_set_enabled", "启用或禁用指定的运维扩展服务（禁用后拦截动作执行）", ExtensionSetEnabledSchema),
            new McpToolDefinition("extension_check_update", "检查指定或全部运维扩展服务的远程发布版本", ExtensionCheckUpdateSchema),
            new McpToolDefinition("extension_hot_update", "对指定运维扩展服务执行安全在线热更新（带备份与自动回滚）", ExtensionHotUpdateSchema)
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

            case "extension_execute_action":
                return await HandleExtensionExecuteActionAsync(args, context);

            case "extension_import":
                return await HandleExtensionImportAsync(args, context);

            case "extension_set_enabled":
                return await HandleExtensionSetEnabledAsync(args, context);

            case "extension_check_update":
                return await HandleExtensionCheckUpdateAsync(args);

            case "extension_hot_update":
                return await HandleExtensionHotUpdateAsync(args, context);

            default:
                return null;
        }
    }

    private async Task<McpToolExecutionResult> HandleServicesListAsync(JsonElement args)
    {
        var category = args.TryGetProperty("category", out var cProp) ? cProp.GetString() ?? "all" : "all";
        var sb = new StringBuilder();

        if (category is "all" or "extension")
        {
            var exts = await extensionManager.ListExtensionsAsync();
            sb.AppendLine($"=== 运维扩展服务 ({exts.Count} 个) ===");
            foreach (var ext in exts)
            {
                var flag = ext.IsEnabled ? "已启用" : "已禁用";
                sb.AppendLine($"- [{ext.Manifest.Id}] {ext.Manifest.Name} (v{ext.Manifest.Version}) - 状态: {ext.Status} ({flag})");
                if (!string.IsNullOrEmpty(ext.StatusMessage)) sb.AppendLine($"  说明: {ext.StatusMessage}");
            }
            sb.AppendLine();
        }

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

        return res.Success
            ? McpToolExecutionResult.Success($"Systemd 服务 '{serviceName}' 执行 '{actionStr}' 成功: {res.Message}")
            : McpToolExecutionResult.Error($"Systemd 服务 '{serviceName}' 执行 '{actionStr}' 失败: {res.Message}");
    }

    private async Task<McpToolExecutionResult> HandleDockerActionAsync(JsonElement args, McpRequestContext context)
    {
        if (!args.TryGetProperty("container_id", out var cProp) || string.IsNullOrWhiteSpace(cProp.GetString()))
            return McpToolExecutionResult.Error("缺少 container_id 参数");
        if (!args.TryGetProperty("action", out var aProp) || string.IsNullOrWhiteSpace(aProp.GetString()))
            return McpToolExecutionResult.Error("缺少 action 参数");

        var containerId = cProp.GetString()!.Trim();
        var action = aProp.GetString()!.Trim().ToLowerInvariant();

        var dockerAction = action switch
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
            Action = dockerAction
        });

        await operationLogger.LogAsync(
            "MCP Docker控制", "容器运维", containerId,
            $"动作: {action}, 结果: {res.Success}, 消息: {res.Message}",
            success: res.Success, clientIp: context.ClientIp);

        return res.Success
            ? McpToolExecutionResult.Success($"Docker 容器 '{containerId}' 执行 '{action}' 成功: {res.Message}")
            : McpToolExecutionResult.Error($"Docker 容器 '{containerId}' 执行 '{action}' 失败: {res.Message}");
    }

    private async Task<McpToolExecutionResult> HandleExtensionExecuteActionAsync(JsonElement args, McpRequestContext context)
    {
        if (!args.TryGetProperty("extension_id", out var eProp) || string.IsNullOrWhiteSpace(eProp.GetString()))
            return McpToolExecutionResult.Error("缺少 extension_id 参数");
        if (!args.TryGetProperty("action", out var aProp) || string.IsNullOrWhiteSpace(aProp.GetString()))
            return McpToolExecutionResult.Error("缺少 action 参数");

        var extensionId = eProp.GetString()!.Trim();
        var action = aProp.GetString()!.Trim();

        var paramsMap = new Dictionary<string, string>();
        if (args.TryGetProperty("params", out var pProp) && pProp.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in pProp.EnumerateObject())
            {
                paramsMap[prop.Name] = prop.Value.GetString() ?? "";
            }
        }

        var res = await extensionManager.ExecuteExtensionActionAsync(new ExtensionActionRequest
        {
            ExtensionId = extensionId,
            Action = action,
            ActionParams = paramsMap
        });

        await operationLogger.LogAsync(
            "MCP 扩展动作执行", "服务扩展", extensionId,
            $"动作: {action}, 结果: {res.Success}, 输出: {res.Message}",
            success: res.Success, clientIp: context.ClientIp);

        return res.Success
            ? McpToolExecutionResult.Success($"扩展 '{extensionId}' 执行动作 '{action}' 成功:\n{res.Message}")
            : McpToolExecutionResult.Error($"扩展 '{extensionId}' 执行动作 '{action}' 失败:\n{res.Message}");
    }

    private async Task<McpToolExecutionResult> HandleExtensionImportAsync(JsonElement args, McpRequestContext context)
    {
        if (!args.TryGetProperty("repo_url", out var rProp) || string.IsNullOrWhiteSpace(rProp.GetString()))
            return McpToolExecutionResult.Error("缺少 repo_url 参数");
        if (!args.TryGetProperty("module_ids", out var mProp) || mProp.ValueKind != JsonValueKind.Array)
            return McpToolExecutionResult.Error("缺少 module_ids 数组参数");

        var repoUrl = rProp.GetString()!.Trim();
        var moduleIds = mProp.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList();
        var overwrite = !args.TryGetProperty("overwrite", out var oProp) || oProp.GetBoolean();

        // 探测仓库获取 module relative paths
        var inspect = await extensionManager.InspectRepositoryModulesAsync(new InspectRepoModulesRequest
        {
            RepositoryUrl = repoUrl,
            TargetType = "extension"
        });

        var selectedPaths = inspect.Modules
            .Where(m => moduleIds.Contains(m.Manifest.Id, StringComparer.OrdinalIgnoreCase))
            .Select(m => m.RelativePath)
            .ToList();

        if (selectedPaths.Count == 0)
        {
            // 兜底直接使用 moduleIds
            selectedPaths = moduleIds.Select(id => $"extensions/{id}").ToList();
        }

        var res = await extensionManager.BatchImportModulesAsync(new BatchImportModulesRequest
        {
            RepositoryUrl = repoUrl,
            SelectedModulePaths = selectedPaths,
            OverwriteExisting = overwrite
        });

        await operationLogger.LogAsync(
            "MCP 导入扩展", "服务扩展", repoUrl,
            $"导入数: {res.ImportedExtensionIds.Count}, 成功: {res.Success}",
            success: res.Success, clientIp: context.ClientIp);

        return res.Success
            ? McpToolExecutionResult.Success($"成功导入扩展: {string.Join(", ", res.ImportedExtensionIds)}")
            : McpToolExecutionResult.Error($"导入扩展失败: {res.Message}");
    }

    private async Task<McpToolExecutionResult> HandleExtensionSetEnabledAsync(JsonElement args, McpRequestContext context)
    {
        if (!args.TryGetProperty("extension_id", out var eProp) || string.IsNullOrWhiteSpace(eProp.GetString()))
            return McpToolExecutionResult.Error("缺少 extension_id 参数");
        if (!args.TryGetProperty("enabled", out var enProp))
            return McpToolExecutionResult.Error("缺少 enabled 参数");

        var extensionId = eProp.GetString()!.Trim();
        var enabled = enProp.GetBoolean();

        var success = await extensionManager.SetEnabledAsync(extensionId, enabled);
        await operationLogger.LogAsync(
            "MCP 扩展启用状态控制", "服务扩展", extensionId,
            $"Enabled: {enabled}, 成功: {success}",
            success: success, clientIp: context.ClientIp);

        return success
            ? McpToolExecutionResult.Success($"扩展 '{extensionId}' 已成功{(enabled ? "启用" : "禁用")}")
            : McpToolExecutionResult.Error($"设置扩展 '{extensionId}' 状态失败");
    }

    private async Task<McpToolExecutionResult> HandleExtensionCheckUpdateAsync(JsonElement args)
    {
        if (args.TryGetProperty("extension_id", out var eProp) && !string.IsNullOrWhiteSpace(eProp.GetString()))
        {
            var id = eProp.GetString()!.Trim();
            var check = await extensionManager.CheckUpdateAsync(id);
            return McpToolExecutionResult.Success($"扩展 [{id}]: 当前版本 v{check.CurrentVersion}, 远程版本: {(check.RemoteVersion != null ? "v" + check.RemoteVersion : "未知")}, 是否有更新: {check.HasUpdate} ({check.ReleaseNotes})");
        }

        var list = await extensionManager.ListExtensionsAsync();
        var sb = new StringBuilder();
        sb.AppendLine("=== 运维扩展服务更新检查结果 ===");
        foreach (var ext in list)
        {
            var check = await extensionManager.CheckUpdateAsync(ext.Manifest.Id);
            sb.AppendLine($"- [{ext.Manifest.Id}] 当前 v{check.CurrentVersion} -> 最新: {(check.RemoteVersion != null ? "v" + check.RemoteVersion : "未关联Git源")}, 更新状态: {(check.HasUpdate ? "⬆️ 可更新" : "最新")}");
        }
        return McpToolExecutionResult.Success(sb.ToString().TrimEnd());
    }

    private async Task<McpToolExecutionResult> HandleExtensionHotUpdateAsync(JsonElement args, McpRequestContext context)
    {
        if (!args.TryGetProperty("extension_id", out var eProp) || string.IsNullOrWhiteSpace(eProp.GetString()))
            return McpToolExecutionResult.Error("缺少 extension_id 参数");

        var extensionId = eProp.GetString()!.Trim();
        var force = args.TryGetProperty("force", out var fProp) && fProp.GetBoolean();

        var res = await extensionManager.HotUpdateAsync(extensionId, new ExtensionHotUpdateRequest(force));

        await operationLogger.LogAsync(
            "MCP 扩展热更新", "服务扩展", extensionId,
            $"结果: {res.Success}, 消息: {res.Message}, 回滚: {res.RollbackPerformed}",
            success: res.Success, clientIp: context.ClientIp);

        return res.Success
            ? McpToolExecutionResult.Success($"扩展 '{extensionId}' 热更新成功: {res.Message} (v{res.OldVersion} -> v{res.NewVersion})")
            : McpToolExecutionResult.Error($"扩展 '{extensionId}' 热更新失败: {res.Message} (已回滚: {res.RollbackPerformed})");
    }
}
