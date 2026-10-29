using System.Text;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.Mcp.Contracts;
using LinuxWebTool.Infrastructure.Shared.Persistence;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using LinuxWebTool.Infrastructure.Shared.Support;

namespace LinuxWebTool.WebHost.Features.Mcp.Engine.Providers;

/// <summary>
/// 定时调度任务 MCP 工具提供者
/// </summary>
public sealed class ScheduleMcpToolProvider(
    ScheduleStore scheduleStore,
    IScheduleManager scheduleManager,
    IOperationLogger operationLogger) : IMcpToolProvider
{
    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static readonly JsonElement ScheduleListSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "enabled_only": { "type": "boolean", "description": "是否仅返回已启用的任务" }
        }
    }
    """);

    private static readonly JsonElement ScheduleIdRequiredSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "schedule_id": { "type": "string", "description": "定时任务 ID" }
        },
        "required": ["schedule_id"]
    }
    """);

    private static readonly JsonElement ScheduleToggleSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "schedule_id": { "type": "string", "description": "定时任务 ID" },
            "enabled": { "type": "boolean", "description": "是否启用" }
        },
        "required": ["schedule_id", "enabled"]
    }
    """);

    private static readonly JsonElement ScheduleCreateSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "name": { "type": "string", "description": "定时任务名称（必填）" },
            "cron": { "type": "string", "description": "标准 Cron 表达式，如 0 0 * * *（必填）" },
            "target_biz_type": { "type": "integer", "enum": [0, 1], "description": "调度目标类型：0=指令(Command), 1=工作流(Workflow)，默认 0" },
            "command_id": { "type": "string", "description": "绑定的指令模版 ID（当 target_biz_type=0 时必填）" },
            "workflow_id": { "type": "string", "description": "绑定的工作流 ID（当 target_biz_type=1 时必填）" },
            "target_host_mode": { "type": "integer", "enum": [0, 1, 2], "description": "执行主机模式：0=本地, 1=单远程主机, 2=多远程主机，默认 0" },
            "target_host_id": { "type": "string", "description": "单远程主机 ID（当 target_host_mode=1 时有效）" },
            "target_host_ids": { "type": "string", "description": "多远程主机 ID 数组 JSON 字符串（当 target_host_mode=2 时有效）" },
            "failure_policy": { "type": "integer", "enum": [0, 1], "description": "失败策略：0=FailFast, 1=Continue，默认 0" },
            "arguments": { "type": "string", "description": "固定执行脚本参数（可选）" }
        },
        "required": ["name", "cron"]
    }
    """);

    public string ModuleName => "schedules";

    public bool IsPermitted(ApiKeyEntity? callerKey) => callerKey == null || callerKey.AllowSchedules;

    public IReadOnlyList<McpToolDefinition> GetTools(ApiKeyEntity? callerKey)
    {
        return
        [
            new McpToolDefinition(
                "schedule_list",
                "查询系统中配置的定时调度任务列表",
                ScheduleListSchema),

            new McpToolDefinition(
                "schedule_get",
                "获取指定定时任务的详细配置与绑定目标",
                ScheduleIdRequiredSchema),

            new McpToolDefinition(
                "schedule_trigger_now",
                "立即触发一次指定的定时任务执行",
                ScheduleIdRequiredSchema),

            new McpToolDefinition(
                "schedule_toggle",
                "启用或暂停指定的定时任务",
                ScheduleToggleSchema),

            new McpToolDefinition(
                "schedule_create",
                "创建新的定时调度任务（支持绑定指令或工作流）",
                ScheduleCreateSchema),

            new McpToolDefinition(
                "schedule_delete",
                "删除指定的定时任务",
                ScheduleIdRequiredSchema)
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
            case "schedule_list":
            {
                var enabledOnly = args.TryGetProperty("enabled_only", out var eoP) && eoP.GetBoolean();
                var tasks = (await scheduleStore.GetAllAsync())
                    .Where(t => !enabledOnly || t.Enabled);

                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(ms))
                {
                    writer.WriteStartArray();
                    foreach (var t in tasks)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("id", t.Id.ToString());
                        writer.WriteString("name", t.Name);
                        writer.WriteString("cron", t.CronExpression);
                        writer.WriteBoolean("enabled", t.Enabled);
                        if (t.NextRunTime.HasValue) writer.WriteString("next_run", t.NextRunTime.Value.ToString("O"));
                        else writer.WriteNull("next_run");
                        if (t.LastRunTime.HasValue) writer.WriteString("last_run", t.LastRunTime.Value.ToString("O"));
                        else writer.WriteNull("last_run");
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                return McpToolExecutionResult.Success(Encoding.UTF8.GetString(ms.ToArray()));
            }

            case "schedule_get":
            {
                if (!args.TryGetProperty("schedule_id", out var sIdProp) || !Guid.TryParse(sIdProp.GetString(), out var schedId))
                    return McpToolExecutionResult.Error("Missing or invalid schedule_id");

                var t = await scheduleStore.GetByIdAsync(schedId);
                if (t == null) return McpToolExecutionResult.Error("Schedule task not found");

                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(ms))
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", t.Id.ToString());
                    writer.WriteString("name", t.Name);
                    writer.WriteString("cron", t.CronExpression);
                    writer.WriteBoolean("enabled", t.Enabled);
                    writer.WriteNumber("target_biz_type", t.TargetBizType);
                    writer.WriteString("command_id", t.CommandId.ToString());
                    if (!string.IsNullOrWhiteSpace(t.WorkflowId)) writer.WriteString("workflow_id", t.WorkflowId);
                    else writer.WriteNull("workflow_id");
                    writer.WriteNumber("target_host_mode", t.TargetHostMode);
                    if (!string.IsNullOrWhiteSpace(t.TargetHostId)) writer.WriteString("target_host_id", t.TargetHostId);
                    else writer.WriteNull("target_host_id");
                    if (!string.IsNullOrWhiteSpace(t.TargetHostIds)) writer.WriteString("target_host_ids", t.TargetHostIds);
                    else writer.WriteNull("target_host_ids");
                    writer.WriteNumber("failure_policy", t.FailurePolicy);
                    if (t.NextRunTime.HasValue) writer.WriteString("next_run", t.NextRunTime.Value.ToString("O"));
                    else writer.WriteNull("next_run");
                    if (t.LastRunTime.HasValue) writer.WriteString("last_run", t.LastRunTime.Value.ToString("O"));
                    else writer.WriteNull("last_run");
                    writer.WriteEndObject();
                }
                return McpToolExecutionResult.Success(Encoding.UTF8.GetString(ms.ToArray()));
            }

            case "schedule_create":
            {
                var name = args.TryGetProperty("name", out var nProp) ? nProp.GetString()?.Trim() : null;
                var cron = args.TryGetProperty("cron", out var cProp) ? cProp.GetString()?.Trim() : null;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(cron))
                    return McpToolExecutionResult.Error("Name and cron expression are required");

                var bizType = args.TryGetProperty("target_biz_type", out var btProp) ? btProp.GetInt32() : 0;
                var cmdId = args.TryGetProperty("command_id", out var ciProp) && Guid.TryParse(ciProp.GetString(), out var cG) ? cG : Guid.Empty;
                var wfId = args.TryGetProperty("workflow_id", out var wiProp) ? wiProp.GetString()?.Trim() : null;
                var hostMode = args.TryGetProperty("target_host_mode", out var hmProp) ? hmProp.GetInt32() : 0;
                var hostId = args.TryGetProperty("target_host_id", out var hiProp) ? hiProp.GetString()?.Trim() : null;
                var hostIds = args.TryGetProperty("target_host_ids", out var hisProp) ? hisProp.GetString()?.Trim() : null;
                var failPolicy = args.TryGetProperty("failure_policy", out var fpProp) ? fpProp.GetInt32() : 0;
                var taskArgs = args.TryGetProperty("arguments", out var aProp) ? aProp.GetString()?.Trim() : null;

                if (bizType == 0 && cmdId == Guid.Empty)
                    return McpToolExecutionResult.Error("command_id is required when target_biz_type is 0");
                if (bizType == 1 && string.IsNullOrWhiteSpace(wfId))
                    return McpToolExecutionResult.Error("workflow_id is required when target_biz_type is 1");

                var task = new ScheduleTask
                {
                    Id = Guid.NewGuid(),
                    Name = name,
                    CronExpression = cron,
                    Enabled = true,
                    TargetBizType = bizType,
                    CommandId = cmdId,
                    WorkflowId = wfId,
                    TargetHostMode = hostMode,
                    TargetHostId = hostId,
                    TargetHostIds = hostIds,
                    FailurePolicy = failPolicy,
                    Arguments = taskArgs,
                    CreateTime = DateTime.UtcNow,
                    UpdateTime = DateTime.UtcNow
                };

                await scheduleStore.InsertAsync(task);
                await scheduleManager.SyncAsync(task);
                await operationLogger.LogAsync("MCP创建定时任务", "定时任务", task.Name, task.Id.ToString(), clientIp: context.ClientIp);

                return McpToolExecutionResult.Success($"{{\"success\":true, \"id\":\"{task.Id}\", \"name\":\"{task.Name}\"}}");
            }

            case "schedule_trigger_now":
            {
                if (!args.TryGetProperty("schedule_id", out var sIdProp))
                    return McpToolExecutionResult.Error("Missing schedule_id");

                if (!Guid.TryParse(sIdProp.GetString(), out var schedId))
                    return McpToolExecutionResult.Error("Invalid schedule_id format");

                var task = await scheduleStore.GetByIdAsync(schedId);
                if (task == null) return McpToolExecutionResult.Error("Schedule task not found");

                await scheduleManager.TriggerNowAsync(task);
                await operationLogger.LogAsync("MCP触发定时任务", "定时任务", task.Name, schedId.ToString(), clientIp: context.ClientIp);
                return McpToolExecutionResult.Success($"{{\"success\":true, \"message\":\"Triggered {task.Name}\"}}");
            }

            case "schedule_toggle":
            {
                if (!args.TryGetProperty("schedule_id", out var sIdProp) || !args.TryGetProperty("enabled", out var enProp))
                    return McpToolExecutionResult.Error("Missing schedule_id or enabled parameter");

                if (!Guid.TryParse(sIdProp.GetString(), out var schedId))
                    return McpToolExecutionResult.Error("Invalid schedule_id format");

                var enabled = enProp.GetBoolean();
                var task = await scheduleStore.GetByIdAsync(schedId);
                if (task == null) return McpToolExecutionResult.Error("Schedule task not found");

                task.Enabled = enabled;
                task.UpdateTime = DateTime.UtcNow;
                await scheduleStore.UpdateAsync(task);

                if (enabled) await scheduleManager.SyncAsync(task);
                else await scheduleManager.RemoveAsync(schedId);

                await operationLogger.LogAsync("MCP切换定时任务", "定时任务", task.Name, $"Enabled={enabled}", clientIp: context.ClientIp);
                return McpToolExecutionResult.Success($"{{\"success\":true, \"enabled\":{enabled.ToString().ToLower()}}}");
            }

            case "schedule_delete":
            {
                if (!args.TryGetProperty("schedule_id", out var sIdProp))
                    return McpToolExecutionResult.Error("Missing schedule_id");

                if (!Guid.TryParse(sIdProp.GetString(), out var schedId))
                    return McpToolExecutionResult.Error("Invalid schedule_id format");

                var task = await scheduleStore.GetByIdAsync(schedId);
                if (task == null) return McpToolExecutionResult.Error("Schedule task not found");

                await scheduleManager.RemoveAsync(schedId);
                await scheduleStore.DeleteAsync(schedId);
                await operationLogger.LogAsync("MCP删除定时任务", "定时任务", task.Name, schedId.ToString(), clientIp: context.ClientIp);

                return McpToolExecutionResult.Success("{\"success\":true, \"message\":\"Deleted successfully\"}");
            }

            default:
                return null;
        }
    }
}
