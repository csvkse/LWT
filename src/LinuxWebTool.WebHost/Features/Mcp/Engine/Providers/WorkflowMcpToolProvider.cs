using System.Text;
using System.Text.Json;
using LinuxWebTool.Application.Features.Security;
using LinuxWebTool.Contracts.Features.Commands.Contracts;
using LinuxWebTool.Contracts.Features.Mcp.Contracts;
using LinuxWebTool.Contracts.Shared.Contracts;
using LinuxWebTool.Infrastructure.Features.Scheduling.Adapters;
using LinuxWebTool.Infrastructure.Shared.Persistence;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using LinuxWebTool.Infrastructure.Shared.Support;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.WebHost.Features.Mcp.Engine.Providers;

/// <summary>
/// 工作流流水线编排 MCP 工具提供者（策略模式 + 提供者模式）
/// </summary>
public sealed class WorkflowMcpToolProvider(
    WorkflowStore workflowStore,
    IUnifiedExecutionEngine executionEngine,
    ExecutionStore executionStore,
    IOperationLogger operationLogger) : IMcpToolProvider
{
    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static readonly JsonElement WorkflowListSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "keyword": { "type": "string", "description": "按名称或描述模糊搜索关键词（可选）" }
        }
    }
    """);

    private static readonly JsonElement WorkflowIdRequiredSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "workflow_id": { "type": "string", "description": "工作流流水线 ID (Guid)" }
        },
        "required": ["workflow_id"]
    }
    """);

    private static readonly JsonElement WorkflowRunSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "workflow_id": { "type": "string", "description": "工作流流水线 ID (Guid)" },
            "target_host_id": { "type": "string", "description": "覆盖指定的目标主机 ID；为空表示使用工作流默认目标或本地主机" }
        },
        "required": ["workflow_id"]
    }
    """);

    private static readonly JsonElement WorkflowGetBatchSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "batch_id": { "type": "string", "description": "执行批次号 (BatchId)" }
        },
        "required": ["batch_id"]
    }
    """);

    private static readonly JsonElement WorkflowToggleSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "workflow_id": { "type": "string", "description": "工作流流水线 ID (Guid)" },
            "enabled": { "type": "boolean", "description": "是否启用" }
        },
        "required": ["workflow_id", "enabled"]
    }
    """);

    public string ModuleName => "workflows";

    public bool IsPermitted(ApiKeyEntity? callerKey) => callerKey == null || callerKey.AllowSchedules;

    public IReadOnlyList<McpToolDefinition> GetTools(ApiKeyEntity? callerKey)
    {
        return
        [
            new McpToolDefinition("workflow_list", "查询系统中已配置的工作流流水线列表", WorkflowListSchema),
            new McpToolDefinition("workflow_get", "查询指定工作流的完整阶段与步骤定义", WorkflowIdRequiredSchema),
            new McpToolDefinition("workflow_run", "触发执行指定的工作流流水线（支持覆盖目标主机）", WorkflowRunSchema),
            new McpToolDefinition("workflow_get_batch", "根据批次号查询工作流执行详情、各步骤耗时与日志", WorkflowGetBatchSchema),
            new McpToolDefinition("workflow_toggle", "启用或暂停指定的工作流流水线", WorkflowToggleSchema)
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
            case "workflow_list":
                return await HandleListAsync(args);

            case "workflow_get":
                return await HandleGetAsync(args);

            case "workflow_run":
                return await HandleRunAsync(args, context);

            case "workflow_get_batch":
                return await HandleGetBatchAsync(args);

            case "workflow_toggle":
                return await HandleToggleAsync(args, context);

            default:
                return null;
        }
    }

    private async Task<McpToolExecutionResult> HandleListAsync(JsonElement args)
    {
        var keyword = args.TryGetProperty("keyword", out var kProp) ? kProp.GetString() : null;
        var workflows = await workflowStore.GetAllAsync();
        var query = workflows.Where(w => string.IsNullOrWhiteSpace(keyword)
            || w.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || (w.Description ?? string.Empty).Contains(keyword, StringComparison.OrdinalIgnoreCase));

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartArray();
            foreach (var w in query)
            {
                writer.WriteStartObject();
                writer.WriteString("id", w.Id.ToString());
                writer.WriteString("name", w.Name);
                writer.WriteString("description", w.Description ?? string.Empty);
                writer.WriteString("execution_mode", w.ExecutionMode.ToString());
                writer.WriteString("failure_policy", w.FailurePolicy.ToString());
                if (!string.IsNullOrWhiteSpace(w.DefaultTargetHostId)) writer.WriteString("default_target_host_id", w.DefaultTargetHostId);
                else writer.WriteNull("default_target_host_id");
                writer.WriteBoolean("enabled", w.Enabled);
                if (w.LastRunTime.HasValue) writer.WriteString("last_run_time", w.LastRunTime.Value.ToString("O"));
                else writer.WriteNull("last_run_time");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return McpToolExecutionResult.Success(Encoding.UTF8.GetString(ms.ToArray()));
    }

    private async Task<McpToolExecutionResult> HandleGetAsync(JsonElement args)
    {
        if (!args.TryGetProperty("workflow_id", out var idProp) || !Guid.TryParse(idProp.GetString(), out var id))
            return McpToolExecutionResult.Error("Missing or invalid workflow_id");

        var w = await workflowStore.GetByIdAsync(id);
        if (w == null) return McpToolExecutionResult.Error($"Workflow not found: '{id}'");

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("id", w.Id.ToString());
            writer.WriteString("name", w.Name);
            writer.WriteString("description", w.Description ?? string.Empty);
            writer.WriteString("execution_mode", w.ExecutionMode.ToString());
            writer.WriteString("failure_policy", w.FailurePolicy.ToString());
            if (!string.IsNullOrWhiteSpace(w.DefaultTargetHostId)) writer.WriteString("default_target_host_id", w.DefaultTargetHostId);
            else writer.WriteNull("default_target_host_id");
            writer.WriteString("definition_json", w.DefinitionJson);
            writer.WriteBoolean("enabled", w.Enabled);
            writer.WriteEndObject();
        }
        return McpToolExecutionResult.Success(Encoding.UTF8.GetString(ms.ToArray()));
    }

    private async Task<McpToolExecutionResult> HandleRunAsync(JsonElement args, McpRequestContext context)
    {
        if (!args.TryGetProperty("workflow_id", out var idProp) || !Guid.TryParse(idProp.GetString(), out var id))
            return McpToolExecutionResult.Error("Missing or invalid workflow_id");

        var w = await workflowStore.GetByIdAsync(id);
        if (w == null) return McpToolExecutionResult.Error($"Workflow not found: '{id}'");

        var targetHostId = args.TryGetProperty("target_host_id", out var thProp) ? thProp.GetString()?.Trim() : null;
        var effectiveHostId = !string.IsNullOrWhiteSpace(targetHostId) ? targetHostId : w.DefaultTargetHostId;

        if (!string.IsNullOrWhiteSpace(effectiveHostId) && context.CallerKey != null &&
            !ApiKeyPermissionMatrix.CanAccessRemoteHost(context.CallerKey.AllowRemoteHosts, context.CallerKey.AllowedHostIds, effectiveHostId))
        {
            return McpToolExecutionResult.Error($"Permission Denied: API Key is not authorized to access host '{effectiveHostId}'.");
        }

        ExecutionTarget? targetOverride = !string.IsNullOrWhiteSpace(effectiveHostId)
            ? ExecutionTarget.Remote(effectiveHostId)
            : null;

        var triggerBy = "mcp:" + (context.CallerKey?.Name ?? "agent");
        var resp = await executionEngine.ExecuteWorkflowAsync(id, triggerBy, targetOverride, context.CancellationToken);

        await operationLogger.LogAsync("MCP运行工作流", "工作流", w.Name,
            $"BatchId={resp.BatchId}, OverallStatus={resp.OverallStatus}, Steps={resp.Results.Count}",
            success: resp.OverallStatus == ExecutionStatus.Success, clientIp: context.ClientIp);

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("batch_id", resp.BatchId);
            writer.WriteString("overall_status", resp.OverallStatus.ToString());
            writer.WriteNumber("duration_ms", resp.DurationMs);
            writer.WriteStartArray("results");
            foreach (var r in resp.Results)
            {
                writer.WriteStartObject();
                writer.WriteString("command_name", r.CommandName);
                writer.WriteString("status", r.Status.ToString());
                if (r.ExitCode.HasValue) writer.WriteNumber("exit_code", r.ExitCode.Value);
                else writer.WriteNull("exit_code");
                writer.WriteNumber("duration_ms", r.DurationMs);
                writer.WriteString("output", r.Output);
                writer.WriteString("error_output", r.ErrorOutput);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return McpToolExecutionResult.Success(Encoding.UTF8.GetString(ms.ToArray()));
    }

    private async Task<McpToolExecutionResult> HandleGetBatchAsync(JsonElement args)
    {
        var batchId = args.TryGetProperty("batch_id", out var bProp) ? bProp.GetString()?.Trim() : null;
        if (string.IsNullOrWhiteSpace(batchId)) return McpToolExecutionResult.Error("Missing batch_id");

        var records = (await executionStore.GetByBatchIdAsync(batchId)).ToList();
        if (records.Count == 0) return McpToolExecutionResult.Error($"No execution records found for batch '{batchId}'");

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("batch_id", batchId);
            writer.WriteNumber("step_count", records.Count);
            writer.WriteStartArray("steps");
            foreach (var r in records)
            {
                writer.WriteStartObject();
                writer.WriteString("id", r.Id.ToString());
                writer.WriteString("command_name", r.CommandName);
                writer.WriteString("status", ((ExecutionStatus)r.Status).ToString());
                if (r.ExitCode.HasValue) writer.WriteNumber("exit_code", r.ExitCode.Value);
                else writer.WriteNull("exit_code");
                writer.WriteNumber("duration_ms", r.DurationMs);
                if (!string.IsNullOrWhiteSpace(r.TargetHostName)) writer.WriteString("target_host_name", r.TargetHostName);
                writer.WriteString("output", r.Output);
                writer.WriteString("error_output", r.ErrorOutput);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return McpToolExecutionResult.Success(Encoding.UTF8.GetString(ms.ToArray()));
    }

    private async Task<McpToolExecutionResult> HandleToggleAsync(JsonElement args, McpRequestContext context)
    {
        if (!args.TryGetProperty("workflow_id", out var idProp) || !Guid.TryParse(idProp.GetString(), out var id))
            return McpToolExecutionResult.Error("Missing or invalid workflow_id");

        if (!args.TryGetProperty("enabled", out var enProp))
            return McpToolExecutionResult.Error("Missing enabled parameter");

        var w = await workflowStore.GetByIdAsync(id);
        if (w == null) return McpToolExecutionResult.Error($"Workflow not found: '{id}'");

        var enabled = enProp.GetBoolean();
        await workflowStore.ToggleEnabledAsync(id, enabled);
        await operationLogger.LogAsync("MCP切换工作流", "工作流", w.Name, $"Enabled={enabled}", clientIp: context.ClientIp);

        return McpToolExecutionResult.Success($"{{\"success\":true, \"enabled\":{enabled.ToString().ToLower()}}}");
    }
}