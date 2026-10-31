using System.Text;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.Mcp.Contracts;
using LinuxWebTool.Contracts.Features.Workers.Contracts;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using LinuxWebTool.Infrastructure.Shared.Support;

namespace LinuxWebTool.WebHost.Features.Mcp.Engine.Providers;

/// <summary>
/// SaaS 函数微服务与常驻守护平台 (Workers) MCP 工具提供者
/// </summary>
public sealed partial class WorkersMcpToolProvider(
    IWorkersManager workersManager,
    IHttpClientFactory httpClientFactory,
    IOperationLogger operationLogger) : IMcpToolProvider
{
    public string ModuleName => "workers";

    public bool IsPermitted(ApiKeyEntity? callerKey) => callerKey == null || callerKey.AllowWorkers;

    public IReadOnlyList<McpToolDefinition> GetTools(ApiKeyEntity? callerKey)
    {
        return
        [
            new McpToolDefinition("workers_list", "列出所有 SaaS 函数微服务与长时守护进程的状态、端口、指标与启用状态", EmptyObjectSchema),
            new McpToolDefinition("worker_control", "启动、停止或重启指定的函数微服务或常驻守护", WorkerControlSchema),
            new McpToolDefinition("worker_invoke", "向指定的 HTTP 函数微服务发起内部调用并返回响应结果", WorkerInvokeSchema),
            new McpToolDefinition("worker_get_logs", "获取指定 Worker 进程最新的标准输出与异常错误日志", WorkerGetLogsSchema),
            new McpToolDefinition("worker_import", "从 GitHub/Git 仓库批量导入函数微服务或后台长时守护", WorkerImportSchema),
            new McpToolDefinition("worker_set_enabled", "启用或禁用指定 Worker（禁用时终止进程、关闭网关并暂停自愈）", WorkerSetEnabledSchema),
            new McpToolDefinition("worker_check_update", "检查指定 Worker 或全部 Worker 的最新远程发布版本", WorkerCheckUpdateSchema),
            new McpToolDefinition("worker_hot_update", "对指定 Worker 执行安全在线热更新（保护 SQLite/KV 数据并带瞬退回滚）", WorkerHotUpdateSchema),
            new McpToolDefinition("worker_get_code", "查询 Worker 的指定代码文件内容与文件目录列表", WorkerGetCodeSchema),
            new McpToolDefinition("worker_edit_code", "在线编辑 Worker 代码/配置文件（自动备份快照，可选项保存后自动重启）", WorkerEditCodeSchema),
            new McpToolDefinition("worker_create", "快速创建全新的 SaaS 函数微服务或常驻后台守护", WorkerCreateSchema),
            new McpToolDefinition("worker_quick_create", "以单文件极速模式创建或部署 Worker（支持头部以 @name, @description, @runtime, @mode, @port, @target JSDoc/注释自省声明元数据，无需多层目录结构或zip脚手架）", WorkerQuickCreateSchema),
            new McpToolDefinition("worker_debug_run", "瞬态沙箱调试执行 Worker，捕获实时标准输出、标准错误与耗时（不干扰生产守护）", WorkerDebugRunSchema)
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
            case "workers_list":
                return await HandleWorkersListAsync();

            case "worker_control":
                return await HandleWorkerControlAsync(args, context);

            case "worker_invoke":
                return await HandleWorkerInvokeAsync(args, context);

            case "worker_get_logs":
                return await HandleWorkerGetLogsAsync(args);

            case "worker_import":
                return await HandleWorkerImportAsync(args, context);

            case "worker_set_enabled":
                return await HandleWorkerSetEnabledAsync(args, context);

            case "worker_check_update":
                return await HandleWorkerCheckUpdateAsync(args);

            case "worker_hot_update":
                return await HandleWorkerHotUpdateAsync(args, context);

            case "worker_get_code":
                return await HandleWorkerGetCodeAsync(args);

            case "worker_edit_code":
                return await HandleWorkerEditCodeAsync(args, context);

            case "worker_create":
                return await HandleWorkerCreateAsync(args, context);

            case "worker_debug_run":
                return await HandleWorkerDebugRunAsync(args);

            default:
                return null;
        }
    }

    private async Task<McpToolExecutionResult> HandleWorkersListAsync()
    {
        var workers = await workersManager.GetAllAsync();
        var sb = new StringBuilder();
        sb.AppendLine($"=== SaaS 函数微服务与守护平台 ({workers.Count} 个) ===");

        foreach (var w in workers)
        {
            var flag = w.IsEnabled ? "已启用" : "已禁用";
            var modeStr = w.Mode == WorkerServiceMode.Service ? "微服务(HTTP)" : "后台守护";
            sb.AppendLine($"- [{w.Id}] {w.Name} ({w.Runtime}, {modeStr}) - 状态: {w.State} ({flag})");
            if (w.BoundPort.HasValue) sb.AppendLine($"  端口: {w.BoundPort.Value}, 路径: /w/{w.Id}/");
            if (w.ProcessId.HasValue) sb.AppendLine($"  PID: {w.ProcessId.Value}, 运行时间: {w.UptimeSeconds} 秒");
            if (!string.IsNullOrEmpty(w.LastError)) sb.AppendLine($"  错误: {w.LastError}");
        }

        return McpToolExecutionResult.Success(sb.ToString().TrimEnd());
    }

    private async Task<McpToolExecutionResult> HandleWorkerControlAsync(JsonElement args, McpRequestContext context)
    {
        if (!args.TryGetProperty("worker_id", out var wProp) || string.IsNullOrWhiteSpace(wProp.GetString()))
            return McpToolExecutionResult.Error("缺少 worker_id 参数");
        if (!args.TryGetProperty("action", out var aProp) || string.IsNullOrWhiteSpace(aProp.GetString()))
            return McpToolExecutionResult.Error("缺少 action 参数");

        var workerId = wProp.GetString()!.Trim();
        var action = aProp.GetString()!.Trim().ToLowerInvariant();

        bool success = action switch
        {
            "start" => await workersManager.StartAsync(workerId),
            "stop" => await workersManager.StopAsync(workerId),
            "restart" => await workersManager.RestartAsync(workerId),
            _ => false
        };

        await operationLogger.LogAsync(
            "MCP Worker控制", "函数服务", workerId,
            $"动作: {action}, 结果: {success}",
            success: success, clientIp: context.ClientIp);

        return success
            ? McpToolExecutionResult.Success($"Worker '{workerId}' 执行 '{action}' 成功")
            : McpToolExecutionResult.Error($"Worker '{workerId}' 执行 '{action}' 失败，请查看日志");
    }

    private async Task<McpToolExecutionResult> HandleWorkerInvokeAsync(JsonElement args, McpRequestContext context)
    {
        if (!args.TryGetProperty("worker_id", out var wProp) || string.IsNullOrWhiteSpace(wProp.GetString()))
            return McpToolExecutionResult.Error("缺少 worker_id 参数");

        var workerId = wProp.GetString()!.Trim();
        var subPath = args.TryGetProperty("sub_path", out var spProp) ? spProp.GetString()?.TrimStart('/') ?? "" : "";
        var method = args.TryGetProperty("method", out var mProp) ? mProp.GetString()?.ToUpperInvariant() ?? "GET" : "GET";
        var body = args.TryGetProperty("body", out var bProp) ? bProp.GetString() : null;

        var worker = await workersManager.GetByIdAsync(workerId);
        if (worker == null)
            return McpToolExecutionResult.Error($"未找到 Worker '{workerId}'");
        if (!worker.IsEnabled)
            return McpToolExecutionResult.Error($"Worker '{workerId}' 已被显式禁用，无法发起调用");
        if (worker.State != WorkerState.Running || !worker.BoundPort.HasValue)
            return McpToolExecutionResult.Error($"Worker '{workerId}' 当前未处于 Running 状态或未分配端口");

        try
        {
            var client = httpClientFactory.CreateClient();
            var uri = $"http://127.0.0.1:{worker.BoundPort.Value}/{subPath}";
            using var req = new HttpRequestMessage(new HttpMethod(method), uri);
            if (!string.IsNullOrWhiteSpace(body) && (method == "POST" || method == "PUT" || method == "PATCH"))
            {
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            using var resp = await client.SendAsync(req);
            var content = await resp.Content.ReadAsStringAsync();

            return resp.IsSuccessStatusCode
                ? McpToolExecutionResult.Success($"HTTP {(int)resp.StatusCode} OK:\n{content}")
                : McpToolExecutionResult.Error($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}:\n{content}");
        }
        catch (Exception ex)
        {
            return McpToolExecutionResult.Error($"调用 Worker '{workerId}' 异常: {ex.Message}");
        }
    }

    private async Task<McpToolExecutionResult> HandleWorkerGetLogsAsync(JsonElement args)
    {
        if (!args.TryGetProperty("worker_id", out var wProp) || string.IsNullOrWhiteSpace(wProp.GetString()))
            return McpToolExecutionResult.Error("缺少 worker_id 参数");

        var workerId = wProp.GetString()!.Trim();
        var lines = args.TryGetProperty("lines", out var lProp) ? Math.Clamp(lProp.GetInt32(), 1, 500) : 50;

        var logs = await workersManager.GetLogsAsync(workerId, lines);
        return McpToolExecutionResult.Success(string.Join("\n", logs));
    }

    private async Task<McpToolExecutionResult> HandleWorkerImportAsync(JsonElement args, McpRequestContext context)
    {
        if (!args.TryGetProperty("repo_url", out var rProp) || string.IsNullOrWhiteSpace(rProp.GetString()))
            return McpToolExecutionResult.Error("缺少 repo_url 参数");
        if (!args.TryGetProperty("module_ids", out var mProp) || mProp.ValueKind != JsonValueKind.Array)
            return McpToolExecutionResult.Error("缺少 module_ids 数组参数");

        var repoUrl = rProp.GetString()!.Trim();
        var moduleIds = mProp.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList();
        var overwrite = !args.TryGetProperty("overwrite", out var oProp) || oProp.GetBoolean();

        // 1. 扫描仓库
        var scan = await workersManager.ScanRepoAsync(repoUrl, targetType: "worker");
        if (string.IsNullOrEmpty(scan.TempDirectory))
        {
            return McpToolExecutionResult.Error("扫描仓库失败或临时目录不可用");
        }

        // 2. 批量导入
        var count = await workersManager.ApplyRepoModulesAsync(new WorkerRepoApplyRequest
        {
            TempDirectory = scan.TempDirectory,
            ModuleIds = moduleIds,
            OverwriteExisting = overwrite,
            RepoUrl = repoUrl
        });

        await operationLogger.LogAsync(
            "MCP 导入 Worker", "函数服务", repoUrl,
            $"导入数: {count}",
            success: count > 0, clientIp: context.ClientIp);

        return count > 0
            ? McpToolExecutionResult.Success($"成功导入 {count} 个 Worker 模块")
            : McpToolExecutionResult.Error("未能导入任何 Worker 模块，请检查 module_ids 是否匹配");
    }

    private async Task<McpToolExecutionResult> HandleWorkerSetEnabledAsync(JsonElement args, McpRequestContext context)
    {
        if (!args.TryGetProperty("worker_id", out var wProp) || string.IsNullOrWhiteSpace(wProp.GetString()))
            return McpToolExecutionResult.Error("缺少 worker_id 参数");
        if (!args.TryGetProperty("enabled", out var enProp))
            return McpToolExecutionResult.Error("缺少 enabled 参数");

        var workerId = wProp.GetString()!.Trim();
        var enabled = enProp.GetBoolean();

        var success = await workersManager.SetEnabledAsync(workerId, enabled);
        await operationLogger.LogAsync(
            "MCP Worker启用状态控制", "函数服务", workerId,
            $"Enabled: {enabled}, 成功: {success}",
            success: success, clientIp: context.ClientIp);

        return success
            ? McpToolExecutionResult.Success($"Worker '{workerId}' 已成功{(enabled ? "启用" : "禁用")}")
            : McpToolExecutionResult.Error($"设置 Worker '{workerId}' 状态失败");
    }

    private async Task<McpToolExecutionResult> HandleWorkerCheckUpdateAsync(JsonElement args)
    {
        if (args.TryGetProperty("worker_id", out var wProp) && !string.IsNullOrWhiteSpace(wProp.GetString()))
        {
            var id = wProp.GetString()!.Trim();
            var check = await workersManager.CheckUpdateAsync(id);
            return McpToolExecutionResult.Success($"Worker [{id}]: 当前版本 v{check.CurrentVersion}, 远程版本: {(check.RemoteVersion != null ? "v" + check.RemoteVersion : "未知")}, 是否有更新: {check.HasUpdate} ({check.ReleaseNotes})");
        }

        var list = await workersManager.GetAllAsync();
        var sb = new StringBuilder();
        sb.AppendLine("=== SaaS 函数微服务更新检查结果 ===");
        foreach (var w in list)
        {
            var check = await workersManager.CheckUpdateAsync(w.Id);
            sb.AppendLine($"- [{w.Id}] 当前 v{check.CurrentVersion} -> 最新: {(check.RemoteVersion != null ? "v" + check.RemoteVersion : "未关联Git源")}, 更新状态: {(check.HasUpdate ? "⬆️ 可更新" : "最新")}");
        }
        return McpToolExecutionResult.Success(sb.ToString().TrimEnd());
    }

    private async Task<McpToolExecutionResult> HandleWorkerHotUpdateAsync(JsonElement args, McpRequestContext context)
    {
        if (!args.TryGetProperty("worker_id", out var wProp) || string.IsNullOrWhiteSpace(wProp.GetString()))
            return McpToolExecutionResult.Error("缺少 worker_id 参数");

        var workerId = wProp.GetString()!.Trim();
        var force = args.TryGetProperty("force", out var fProp) && fProp.GetBoolean();

        var res = await workersManager.HotUpdateAsync(workerId, new WorkerHotUpdateRequest(force));

        await operationLogger.LogAsync(
            "MCP Worker热更新", "函数服务", workerId,
            $"结果: {res.Success}, 消息: {res.Message}, 回滚: {res.RollbackPerformed}",
            success: res.Success, clientIp: context.ClientIp);

        return res.Success
            ? McpToolExecutionResult.Success($"Worker '{workerId}' 热更新成功: {res.Message} (v{res.OldVersion} -> v{res.NewVersion})")
            : McpToolExecutionResult.Error($"Worker '{workerId}' 热更新失败: {res.Message} (已回滚: {res.RollbackPerformed})");
    }
}
