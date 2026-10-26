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
                "schedule_trigger_now",
                "立即触发一次指定的定时任务执行",
                ScheduleIdRequiredSchema),

            new McpToolDefinition(
                "schedule_toggle",
                "启用或暂停指定的定时任务",
                ScheduleToggleSchema),

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
