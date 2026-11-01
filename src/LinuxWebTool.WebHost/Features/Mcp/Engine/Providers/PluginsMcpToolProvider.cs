using System.Text;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.Mcp.Contracts;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using LinuxWebTool.Infrastructure.Shared.Support;

namespace LinuxWebTool.WebHost.Features.Mcp.Engine.Providers;

/// <summary>
/// 统一插件平台 (Plugin Platform) MCP 工具提供者
/// </summary>
public sealed partial class PluginsMcpToolProvider(
    IPluginManager pluginManager,
    IHttpClientFactory httpClientFactory,
    IOperationLogger operationLogger) : IMcpToolProvider
{
    public string ModuleName => "plugins";

    public bool IsPermitted(ApiKeyEntity? callerKey) => callerKey == null || callerKey.AllowPlugins;

    public IReadOnlyList<McpToolDefinition> GetTools(ApiKeyEntity? callerKey)
    {
        return
        [
            new McpToolDefinition("plugins_list", "列出所有插件（常驻微服务与轻量脚本扩展）的状态、端口、指标与启用状态", EmptyObjectSchema),
            new McpToolDefinition("plugin_control", "启动、停止或重启指定的常驻插件微服务", PluginControlSchema),
            new McpToolDefinition("plugin_invoke", "向指定的 HTTP 插件微服务发起内部调用并返回响应结果", PluginInvokeSchema),
            new McpToolDefinition("plugin_action", "执行指定插件的自定义动作钩子（支持参数传递）", PluginActionSchema),
            new McpToolDefinition("plugin_get_logs", "获取指定插件进程最新的标准输出与异常错误日志", PluginGetLogsSchema),
            new McpToolDefinition("plugin_set_enabled", "启用或禁用指定插件（禁用时终止进程、关闭网关并暂停自愈）", PluginSetEnabledSchema),
            new McpToolDefinition("plugin_get_code", "查询插件的代码文件内容与文件目录列表", PluginGetCodeSchema),
            new McpToolDefinition("plugin_edit_code", "在线编辑插件代码/配置文件（自动备份快照）", PluginEditCodeSchema),
            new McpToolDefinition("plugin_debug_run", "瞬态沙箱调试执行插件动作，捕获实时标准输出、标准错误与耗时", PluginDebugRunSchema)
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
            case "plugins_list":
                return await HandlePluginsListAsync();

            case "plugin_control":
                return await HandlePluginControlAsync(args, context);

            case "plugin_invoke":
                return await HandlePluginInvokeAsync(args, context);

            case "plugin_action":
                return await HandlePluginActionAsync(args, context);

            case "plugin_get_logs":
                return await HandlePluginGetLogsAsync(args);

            case "plugin_set_enabled":
                return await HandlePluginSetEnabledAsync(args, context);

            case "plugin_get_code":
                return await HandlePluginGetCodeAsync(args);

            case "plugin_edit_code":
                return await HandlePluginEditCodeAsync(args, context);

            case "plugin_debug_run":
                return await HandlePluginDebugRunAsync(args);

            default:
                return null;
        }
    }

    private async Task<McpToolExecutionResult> HandlePluginsListAsync()
    {
        var list = await pluginManager.GetAllAsync();
        var sb = new StringBuilder();
        sb.AppendLine($"=== 插件列表 ({list.Count} 个) ===");
        foreach (var p in list)
        {
            var flag = p.Manifest.IsEnabled ? "已启用" : "已禁用";
            var portStr = p.AllocatedPort.HasValue ? $", 端口: {p.AllocatedPort.Value}" : "";
            sb.AppendLine($"- [{p.Manifest.Id}] {p.Manifest.Name} (v{p.Manifest.Version}) - 规格: {p.Manifest.Profile}, 状态: {p.State}{portStr} ({flag})");
            if (!string.IsNullOrEmpty(p.StatusMessage)) sb.AppendLine($"  提示: {p.StatusMessage}");
        }
        return McpToolExecutionResult.Success(sb.ToString().TrimEnd());
    }

    private async Task<McpToolExecutionResult> HandlePluginControlAsync(JsonElement args, McpRequestContext context)
    {
        if (!args.TryGetProperty("plugin_id", out var pProp) || string.IsNullOrWhiteSpace(pProp.GetString()))
            return McpToolExecutionResult.Error("缺少 plugin_id 参数");
        if (!args.TryGetProperty("action", out var aProp) || string.IsNullOrWhiteSpace(aProp.GetString()))
            return McpToolExecutionResult.Error("缺少 action 参数");

        var pluginId = pProp.GetString()!.Trim();
        var action = aProp.GetString()!.Trim().ToLowerInvariant();

        bool success;
        string? errorMsg = null;
        switch (action)
        {
            case "start":
                (success, errorMsg) = await pluginManager.StartWithResultAsync(pluginId);
                break;
            case "stop":
                success = await pluginManager.StopAsync(pluginId);
                break;
            case "restart":
                success = await pluginManager.RestartAsync(pluginId);
                break;
            default:
                return McpToolExecutionResult.Error($"未知控制动作: {action} (仅支持 start, stop, restart)");
        }

        await operationLogger.LogAsync(
            "MCP 插件控制", "插件中心", pluginId,
            $"动作: {action}, 成功: {success}",
            success: success, clientIp: context.ClientIp);

        return success
            ? McpToolExecutionResult.Success($"插件 '{pluginId}' 执行 {action} 成功")
            : McpToolExecutionResult.Error($"插件 '{pluginId}' 执行 {action} 失败: {errorMsg ?? "请检查日志"}");
    }

    private async Task<McpToolExecutionResult> HandlePluginInvokeAsync(JsonElement args, McpRequestContext context)
    {
        if (!args.TryGetProperty("plugin_id", out var pProp) || string.IsNullOrWhiteSpace(pProp.GetString()))
            return McpToolExecutionResult.Error("缺少 plugin_id 参数");

        var pluginId = pProp.GetString()!.Trim();
        var plugin = await pluginManager.GetByIdAsync(pluginId);
        if (plugin == null) return McpToolExecutionResult.Error($"未找到插件: {pluginId}");
        if (plugin.State != PluginState.Running || !plugin.AllocatedPort.HasValue)
            return McpToolExecutionResult.Error($"插件 '{pluginId}' 当前未处于运行状态或未分配端口 (当前状态: {plugin.State})");

        var subPath = args.TryGetProperty("path", out var pathProp) ? pathProp.GetString()?.TrimStart('/') ?? "" : "";
        var method = args.TryGetProperty("method", out var mProp) ? mProp.GetString()?.ToUpperInvariant() ?? "GET" : "GET";
        var body = args.TryGetProperty("body", out var bProp) ? bProp.GetString() : null;

        var client = httpClientFactory.CreateClient();
        var uri = $"http://127.0.0.1:{plugin.AllocatedPort.Value}/{subPath}";
        using var req = new HttpRequestMessage(new HttpMethod(method), uri);

        if (!string.IsNullOrWhiteSpace(body) && (method is "POST" or "PUT" or "PATCH"))
        {
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        try
        {
            var resp = await client.SendAsync(req);
            var content = await resp.Content.ReadAsStringAsync();
            return McpToolExecutionResult.Success($"[HTTP {(int)resp.StatusCode} {resp.StatusCode}]\n{content}");
        }
        catch (Exception ex)
        {
            return McpToolExecutionResult.Error($"调用插件端点失败: {ex.Message}");
        }
    }

    private async Task<McpToolExecutionResult> HandlePluginActionAsync(JsonElement args, McpRequestContext context)
    {
        if (!args.TryGetProperty("plugin_id", out var pProp) || string.IsNullOrWhiteSpace(pProp.GetString()))
            return McpToolExecutionResult.Error("缺少 plugin_id 参数");
        if (!args.TryGetProperty("action", out var aProp) || string.IsNullOrWhiteSpace(aProp.GetString()))
            return McpToolExecutionResult.Error("缺少 action 参数");

        var pluginId = pProp.GetString()!.Trim();
        var action = aProp.GetString()!.Trim();
        var param = args.TryGetProperty("param", out var parProp) ? parProp.GetString() : null;

        var res = await pluginManager.ExecuteActionAsync(new PluginActionRequest
        {
            PluginId = pluginId,
            Action = action,
            Param = param
        });

        await operationLogger.LogAsync(
            "MCP 插件动作", "插件中心", pluginId,
            $"动作: {action}, 状态: {res.Status}",
            success: res.Status == "succeeded", clientIp: context.ClientIp);

        return res.Status == "succeeded"
            ? McpToolExecutionResult.Success(res.Message ?? "执行成功")
            : McpToolExecutionResult.Error(res.Error ?? res.Message ?? "执行失败");
    }

    private async Task<McpToolExecutionResult> HandlePluginGetLogsAsync(JsonElement args)
    {
        if (!args.TryGetProperty("plugin_id", out var pProp) || string.IsNullOrWhiteSpace(pProp.GetString()))
            return McpToolExecutionResult.Error("缺少 plugin_id 参数");

        var pluginId = pProp.GetString()!.Trim();
        var limit = args.TryGetProperty("limit", out var lProp) && lProp.TryGetInt32(out var parsed) ? parsed : 200;

        var lines = await pluginManager.GetLogsAsync(pluginId, limit);
        if (lines.Count == 0) return McpToolExecutionResult.Success($"插件 [{pluginId}] 暂无日志输出。");

        var sb = new StringBuilder();
        sb.AppendLine($"=== 插件 [{pluginId}] 最近 {lines.Count} 行日志 ===");
        foreach (var l in lines) sb.AppendLine(l);
        return McpToolExecutionResult.Success(sb.ToString().TrimEnd());
    }

    private async Task<McpToolExecutionResult> HandlePluginSetEnabledAsync(JsonElement args, McpRequestContext context)
    {
        if (!args.TryGetProperty("plugin_id", out var pProp) || string.IsNullOrWhiteSpace(pProp.GetString()))
            return McpToolExecutionResult.Error("缺少 plugin_id 参数");
        if (!args.TryGetProperty("enabled", out var enProp))
            return McpToolExecutionResult.Error("缺少 enabled 参数");

        var pluginId = pProp.GetString()!.Trim();
        var enabled = enProp.GetBoolean();

        var success = await pluginManager.SetEnabledAsync(pluginId, enabled);
        await operationLogger.LogAsync(
            "MCP 插件启用状态控制", "插件中心", pluginId,
            $"Enabled: {enabled}, 成功: {success}",
            success: success, clientIp: context.ClientIp);

        return success
            ? McpToolExecutionResult.Success($"插件 '{pluginId}' 已成功{(enabled ? "启用" : "禁用")}")
            : McpToolExecutionResult.Error($"设置插件 '{pluginId}' 状态失败");
    }

    private async Task<McpToolExecutionResult> HandlePluginGetCodeAsync(JsonElement args)
    {
        if (!args.TryGetProperty("plugin_id", out var pProp) || string.IsNullOrWhiteSpace(pProp.GetString()))
            return McpToolExecutionResult.Error("缺少 plugin_id 参数");

        var pluginId = pProp.GetString()!.Trim();
        var file = args.TryGetProperty("file", out var fProp) ? fProp.GetString() : null;

        try
        {
            var codeDto = await pluginManager.GetCodeAsync(pluginId, file);
            var sb = new StringBuilder();
            sb.AppendLine($"=== 插件 [{pluginId}] 文件: {codeDto.FilePath} ===");
            sb.AppendLine($"可选文件: {string.Join(", ", codeDto.FileTree)}");
            if (codeDto.HasBackup) sb.AppendLine($"具备历史备忘录快照: {codeDto.BackupTimestamp}");
            sb.AppendLine("--- 文件内容 ---");
            sb.AppendLine(codeDto.Content);
            return McpToolExecutionResult.Success(sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            return McpToolExecutionResult.Error($"获取插件 [{pluginId}] 代码失败: {ex.Message}");
        }
    }

    private async Task<McpToolExecutionResult> HandlePluginEditCodeAsync(JsonElement args, McpRequestContext context)
    {
        if (!args.TryGetProperty("plugin_id", out var pProp) || string.IsNullOrWhiteSpace(pProp.GetString()))
            return McpToolExecutionResult.Error("缺少 plugin_id 参数");
        if (!args.TryGetProperty("content", out var cProp))
            return McpToolExecutionResult.Error("缺少 content 参数");

        var pluginId = pProp.GetString()!.Trim();
        var file = args.TryGetProperty("file", out var fProp) ? fProp.GetString() : null;
        var content = cProp.GetString() ?? "";

        try
        {
            var targetFile = file;
            if (string.IsNullOrWhiteSpace(targetFile))
            {
                var cur = await pluginManager.GetCodeAsync(pluginId);
                targetFile = cur.FilePath;
            }

            var success = await pluginManager.SaveCodeAsync(pluginId, new PluginCodeSaveRequest
            {
                FilePath = targetFile,
                Content = content,
                CreateBackup = true
            });

            await operationLogger.LogAsync(
                "MCP 在线代码编辑", "插件中心", pluginId,
                $"文件: {targetFile}, 成功: {success}",
                success: success, clientIp: context.ClientIp);

            return success
                ? McpToolExecutionResult.Success($"插件 [{pluginId}] 文件 {targetFile} 保存成功")
                : McpToolExecutionResult.Error($"插件 [{pluginId}] 文件保存失败");
        }
        catch (Exception ex)
        {
            return McpToolExecutionResult.Error($"编辑插件 [{pluginId}] 代码异常: {ex.Message}");
        }
    }

    private async Task<McpToolExecutionResult> HandlePluginDebugRunAsync(JsonElement args)
    {
        if (!args.TryGetProperty("plugin_id", out var pProp) || string.IsNullOrWhiteSpace(pProp.GetString()))
            return McpToolExecutionResult.Error("缺少 plugin_id 参数");

        var pluginId = pProp.GetString()!.Trim();
        var action = args.TryGetProperty("action", out var aProp) ? aProp.GetString() : null;
        var param = args.TryGetProperty("param", out var parProp) ? parProp.GetString() : null;

        var res = await pluginManager.DebugRunAsync(pluginId, new PluginDebugRunRequest
        {
            Action = action,
            Param = param
        });

        var sb = new StringBuilder();
        sb.AppendLine($"=== 插件 [{pluginId}] 调试运行结果 ===");
        sb.AppendLine($"成功: {res.Success}, 退出码: {res.ExitCode}, 耗时: {res.ElapsedMs:F2}ms");
        if (!string.IsNullOrEmpty(res.Output))
        {
            sb.AppendLine("--- 标准输出 ---");
            sb.AppendLine(res.Output);
        }
        if (!string.IsNullOrEmpty(res.Error))
        {
            sb.AppendLine("--- 标准错误 ---");
            sb.AppendLine(res.Error);
        }

        return McpToolExecutionResult.Success(sb.ToString().TrimEnd());
    }
}
