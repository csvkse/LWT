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
/// Linux 指令模版与批量调度 MCP 工具提供者（策略模式 + 提供者模式）
/// </summary>
public sealed class CommandMcpToolProvider(
    CommandStore commandStore,
    IUnifiedExecutionEngine executionEngine,
    IOperationLogger operationLogger) : IMcpToolProvider
{
    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static readonly JsonElement EmptyObjectSchema = Schema("""{"type":"object","properties":{}}""");

    private static readonly JsonElement CommandListSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "group_id": { "type": "string", "description": "按分组 ID 筛选（可选 Guid）" },
            "keyword": { "type": "string", "description": "按名称或描述模糊搜索关键词（可选）" }
        }
    }
    """);

    private static readonly JsonElement CommandIdRequiredSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "command_id": { "type": "string", "description": "指令模版唯一标识 ID (Guid)" }
        },
        "required": ["command_id"]
    }
    """);

    private static readonly JsonElement CommandRunSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "command_id": { "type": "string", "description": "指令模版唯一标识 ID (Guid)" },
            "target_host_id": { "type": "string", "description": "覆盖指定的目标主机 ID；为空表示使用指令默认配置或本地主机" },
            "arguments": { "type": "string", "description": "动态脚本位置参数（可选，$1 $2...）" },
            "timeout_seconds": { "type": "integer", "description": "执行超时秒数，不传则使用指令默认配置（上限 600）" }
        },
        "required": ["command_id"]
    }
    """);

    private static readonly JsonElement CommandBatchRunSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "mode": { "type": "string", "enum": ["Sequential", "Parallel"], "description": "执行模式：Sequential（串行）或 Parallel（并行），默认 Sequential" },
            "fail_fast": { "type": "boolean", "description": "遇错即停熔断策略，默认 true" },
            "command_ids": {
                "type": "array",
                "items": { "type": "string" },
                "description": "待批量执行的指令模版 ID 列表"
            },
            "target_host_id": { "type": "string", "description": "统一目标主机 ID 覆盖（可选）" }
        },
        "required": ["command_ids"]
    }
    """);

    private static readonly JsonElement CommandCreateSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "name": { "type": "string", "description": "指令名称（必填，上限 100 字符）" },
            "command_text": { "type": "string", "description": "Linux 指令脚本内容（必填）" },
            "description": { "type": "string", "description": "指令用途说明（可选）" },
            "timeout_seconds": { "type": "integer", "description": "执行超时秒数（默认 60）" },
            "default_target_host_id": { "type": "string", "description": "默认绑定的远程主机 ID（可选）" }
        },
        "required": ["name", "command_text"]
    }
    """);

    public string ModuleName => "commands";

    public bool IsPermitted(ApiKeyEntity? callerKey) => callerKey == null || callerKey.AllowTerminal;

    public IReadOnlyList<McpToolDefinition> GetTools(ApiKeyEntity? callerKey)
    {
        return
        [
            new McpToolDefinition("command_list", "查询系统中已配置的指令模版列表", CommandListSchema),
            new McpToolDefinition("command_get", "根据 ID 查询指令模版详细内容与配置", CommandIdRequiredSchema),
            new McpToolDefinition("command_run", "执行指定的指令模版（支持覆盖目标主机与动态参数）", CommandRunSchema),
            new McpToolDefinition("command_batch_run", "批量执行一组指令模版（支持串行/并行模式与熔断策略）", CommandBatchRunSchema),
            new McpToolDefinition("command_create", "创建新的 Linux 指令模版", CommandCreateSchema),
            new McpToolDefinition("command_delete", "删除指定的 Linux 指令模版", CommandIdRequiredSchema)
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
            case "command_list":
                return await HandleListAsync(args);

            case "command_get":
                return await HandleGetAsync(args);

            case "command_run":
                return await HandleRunAsync(args, context);

            case "command_batch_run":
                return await HandleBatchRunAsync(args, context);

            case "command_create":
                return await HandleCreateAsync(args, context);

            case "command_delete":
                return await HandleDeleteAsync(args, context);

            default:
                return null;
        }
    }

    private async Task<McpToolExecutionResult> HandleListAsync(JsonElement args)
    {
        Guid? groupId = args.TryGetProperty("group_id", out var gProp) && Guid.TryParse(gProp.GetString(), out var gid) ? gid : null;
        var keyword = args.TryGetProperty("keyword", out var kProp) ? kProp.GetString() : null;

        var commands = await commandStore.GetAllAsync();
        var query = commands.Where(c => (!groupId.HasValue || c.GroupId == groupId.Value)
            && (string.IsNullOrWhiteSpace(keyword) || c.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) || (c.Description ?? string.Empty).Contains(keyword, StringComparison.OrdinalIgnoreCase)));

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartArray();
            foreach (var c in query)
            {
                writer.WriteStartObject();
                writer.WriteString("id", c.Id.ToString());
                writer.WriteString("name", c.Name);
                writer.WriteString("command_text", c.CommandText);
                writer.WriteNumber("script_type", c.ScriptType);
                writer.WriteString("description", c.Description ?? string.Empty);
                writer.WriteNumber("timeout_seconds", c.TimeoutSeconds ?? 60);
                if (!string.IsNullOrWhiteSpace(c.DefaultTargetHostId)) writer.WriteString("default_target_host_id", c.DefaultTargetHostId);
                else writer.WriteNull("default_target_host_id");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return McpToolExecutionResult.Success(Encoding.UTF8.GetString(ms.ToArray()));
    }

    private async Task<McpToolExecutionResult> HandleGetAsync(JsonElement args)
    {
        if (!args.TryGetProperty("command_id", out var idProp) || !Guid.TryParse(idProp.GetString(), out var id))
            return McpToolExecutionResult.Error("Missing or invalid command_id");

        var cmd = await commandStore.GetByIdAsync(id);
        if (cmd == null) return McpToolExecutionResult.Error($"Command not found: '{id}'");

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("id", cmd.Id.ToString());
            writer.WriteString("name", cmd.Name);
            writer.WriteString("command_text", cmd.CommandText);
            writer.WriteNumber("script_type", cmd.ScriptType);
            writer.WriteString("description", cmd.Description ?? string.Empty);
            writer.WriteNumber("timeout_seconds", cmd.TimeoutSeconds ?? 60);
            if (!string.IsNullOrWhiteSpace(cmd.DefaultTargetHostId)) writer.WriteString("default_target_host_id", cmd.DefaultTargetHostId);
            else writer.WriteNull("default_target_host_id");
            writer.WriteEndObject();
        }
        return McpToolExecutionResult.Success(Encoding.UTF8.GetString(ms.ToArray()));
    }

    private async Task<McpToolExecutionResult> HandleRunAsync(JsonElement args, McpRequestContext context)
    {
        if (!args.TryGetProperty("command_id", out var idProp) || !Guid.TryParse(idProp.GetString(), out var id))
            return McpToolExecutionResult.Error("Missing or invalid command_id");

        var cmd = await commandStore.GetByIdAsync(id);
        if (cmd == null) return McpToolExecutionResult.Error($"Command not found: '{id}'");

        var targetHostId = args.TryGetProperty("target_host_id", out var thProp) ? thProp.GetString() : null;
        var effectiveHostId = !string.IsNullOrWhiteSpace(targetHostId) ? targetHostId.Trim() : cmd.DefaultTargetHostId;

        if (!string.IsNullOrWhiteSpace(effectiveHostId) && context.CallerKey != null &&
            !ApiKeyPermissionMatrix.CanAccessRemoteHost(context.CallerKey.AllowRemoteHosts, context.CallerKey.AllowedHostIds, effectiveHostId))
        {
            return McpToolExecutionResult.Error($"Permission Denied: API Key is not authorized to access host '{effectiveHostId}'.");
        }

        var execTarget = !string.IsNullOrWhiteSpace(effectiveHostId)
            ? ExecutionTarget.Remote(effectiveHostId)
            : ExecutionTarget.Local;

        var dynamicArgs = args.TryGetProperty("arguments", out var aProp) ? aProp.GetString() : null;
        var timeout = args.TryGetProperty("timeout_seconds", out var tProp) ? tProp.GetInt32() : (cmd.TimeoutSeconds ?? 60);

        var triggerBy = "mcp:" + (context.CallerKey?.Name ?? "agent");
        var execContext = new CommandExecutionContext(
            CommandText: cmd.CommandText,
            ScriptType: cmd.ScriptType,
            Arguments: dynamicArgs,
            TimeoutSeconds: Math.Clamp(timeout, 1, 600),
            WorkingDirectory: null,
            Target: execTarget,
            TriggerBy: triggerBy,
            CommandId: cmd.Id);

        var result = await executionEngine.ExecuteCommandAsync(execContext, cmd.Name, context.CancellationToken);

        await operationLogger.LogAsync("MCP运行指令", "指令管理", cmd.Name,
            $"Target={execTarget.TargetType}:{effectiveHostId}, Status={result.Status}, ExitCode={result.ExitCode}",
            success: result.Status == ExecutionStatus.Success, clientIp: context.ClientIp);

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("status", result.Status.ToString());
            if (result.ExitCode.HasValue) writer.WriteNumber("exit_code", result.ExitCode.Value);
            else writer.WriteNull("exit_code");
            writer.WriteNumber("duration_ms", result.DurationMs);
            writer.WriteBoolean("timed_out", result.TimedOut);
            writer.WriteString("output", result.Output);
            writer.WriteString("error_output", result.ErrorOutput);
            writer.WriteEndObject();
        }
        return McpToolExecutionResult.Success(Encoding.UTF8.GetString(ms.ToArray()));
    }

    private async Task<McpToolExecutionResult> HandleBatchRunAsync(JsonElement args, McpRequestContext context)
    {
        if (!args.TryGetProperty("command_ids", out var idsProp) || idsProp.ValueKind != JsonValueKind.Array)
            return McpToolExecutionResult.Error("Missing or invalid command_ids array");

        var modeStr = args.TryGetProperty("mode", out var mProp) ? mProp.GetString() : "Sequential";
        var execMode = string.Equals(modeStr, "Parallel", StringComparison.OrdinalIgnoreCase) ? ExecutionMode.Parallel : ExecutionMode.Sequential;
        var failFast = !args.TryGetProperty("fail_fast", out var ffProp) || ffProp.GetBoolean();
        var overrideHostId = args.TryGetProperty("target_host_id", out var ohProp) ? ohProp.GetString()?.Trim() : null;

        var items = new List<BatchExecutionItem>();
        foreach (var el in idsProp.EnumerateArray())
        {
            if (!Guid.TryParse(el.GetString(), out var cmdId)) continue;
            var cmd = await commandStore.GetByIdAsync(cmdId);
            if (cmd == null) continue;

            var effectiveHost = !string.IsNullOrWhiteSpace(overrideHostId) ? overrideHostId : cmd.DefaultTargetHostId;
            if (!string.IsNullOrWhiteSpace(effectiveHost) && context.CallerKey != null &&
                !ApiKeyPermissionMatrix.CanAccessRemoteHost(context.CallerKey.AllowRemoteHosts, context.CallerKey.AllowedHostIds, effectiveHost))
            {
                return McpToolExecutionResult.Error($"Permission Denied: API Key is not authorized to access host '{effectiveHost}'.");
            }

            items.Add(new BatchExecutionItem(
                cmd.Id,
                cmd.CommandText,
                cmd.ScriptType,
                null,
                cmd.TimeoutSeconds ?? 60,
                effectiveHost,
                cmd.Name));
        }

        if (items.Count == 0) return McpToolExecutionResult.Error("No valid commands found in command_ids array");

        var batchReq = new BatchExecutionRequest
        {
            ExecutionMode = execMode,
            FailurePolicy = failFast ? FailurePolicy.FailFast : FailurePolicy.Continue,
            Commands = items,
            TargetHostId = overrideHostId
        };
        var triggerBy = "mcp:" + (context.CallerKey?.Name ?? "agent");
        var batchResp = await executionEngine.ExecuteBatchAsync(batchReq, triggerBy, context.CancellationToken);

        await operationLogger.LogAsync("MCP批量运行指令", "指令管理", $"模式={execMode}",
            $"BatchId={batchResp.BatchId}, Count={items.Count}, Status={batchResp.OverallStatus}",
            success: batchResp.OverallStatus == ExecutionStatus.Success, clientIp: context.ClientIp);

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("batch_id", batchResp.BatchId);
            writer.WriteString("overall_status", batchResp.OverallStatus.ToString());
            writer.WriteNumber("duration_ms", batchResp.DurationMs);
            writer.WriteStartArray("results");
            foreach (var r in batchResp.Results)
            {
                writer.WriteStartObject();
                writer.WriteString("command_name", r.CommandName);
                writer.WriteString("status", r.Status.ToString());
                if (r.ExitCode.HasValue) writer.WriteNumber("exit_code", r.ExitCode.Value);
                else writer.WriteNull("exit_code");
                writer.WriteNumber("duration_ms", r.DurationMs);
                writer.WriteString("output", r.Output);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return McpToolExecutionResult.Success(Encoding.UTF8.GetString(ms.ToArray()));
    }

    private async Task<McpToolExecutionResult> HandleCreateAsync(JsonElement args, McpRequestContext context)
    {
        var name = args.TryGetProperty("name", out var nProp) ? nProp.GetString()?.Trim() : null;
        var text = args.TryGetProperty("command_text", out var tProp) ? tProp.GetString()?.Trim() : null;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(text))
            return McpToolExecutionResult.Error("Name and command_text are required");

        if (await commandStore.ExistsNameAsync(name, null))
            return McpToolExecutionResult.Error($"Command with name '{name}' already exists");

        var desc = args.TryGetProperty("description", out var dProp) ? dProp.GetString()?.Trim() : null;
        var timeout = args.TryGetProperty("timeout_seconds", out var toProp) ? toProp.GetInt32() : 60;
        var hostId = args.TryGetProperty("default_target_host_id", out var hProp) ? hProp.GetString()?.Trim() : null;

        var cmd = new LinuxCommand
        {
            Id = Guid.NewGuid(),
            Name = name,
            CommandText = text,
            Description = desc,
            TimeoutSeconds = Math.Clamp(timeout, 1, 600),
            DefaultTargetHostId = hostId
        };

        await commandStore.InsertAsync(cmd);
        await operationLogger.LogAsync("MCP新建指令", "指令管理", cmd.Name, cmd.Id.ToString(), clientIp: context.ClientIp);

        return McpToolExecutionResult.Success($"{{\"success\":true, \"id\":\"{cmd.Id}\", \"name\":\"{cmd.Name}\"}}");
    }

    private async Task<McpToolExecutionResult> HandleDeleteAsync(JsonElement args, McpRequestContext context)
    {
        if (!args.TryGetProperty("command_id", out var idProp) || !Guid.TryParse(idProp.GetString(), out var id))
            return McpToolExecutionResult.Error("Missing or invalid command_id");

        var cmd = await commandStore.GetByIdAsync(id);
        if (cmd == null) return McpToolExecutionResult.Error($"Command not found: '{id}'");

        await commandStore.DeleteAsync(id);
        await operationLogger.LogAsync("MCP删除指令", "指令管理", cmd.Name, id.ToString(), clientIp: context.ClientIp);

        return McpToolExecutionResult.Success("{\"success\":true, \"message\":\"Deleted successfully\"}");
    }
}