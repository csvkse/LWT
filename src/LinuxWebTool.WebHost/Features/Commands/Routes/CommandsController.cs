using LinuxWebTool.Contracts.Features.Commands.Contracts;
using LinuxWebTool.Contracts.Shared.Contracts;
using LinuxWebTool.Infrastructure.Features.Scheduling.Adapters;
using LinuxWebTool.Infrastructure.Shared.Persistence;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using LinuxWebTool.Infrastructure.Shared.Support;

namespace LinuxWebTool.WebHost.Features.Commands.Routes;

/// <summary>指令功能：保存 Linux 指令的增删改查、执行、快速执行、批量编排执行与执行历史。</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class CommandsController(
    CommandStore commandStore,
    GroupStore groupStore,
    ExecutionStore executionStore,
    IUnifiedExecutionEngine executionEngine,
    IOperationLogger operationLogger) : MinimalApi.ControllerBase
{
    [HttpGet]
    public async Task<IResult> GetAll([FromQuery] Guid? groupId, [FromQuery] string? keyword)
    {
        var commands = await commandStore.GetAllAsync();
        IEnumerable<LinuxCommand> query = commands;
        if (groupId.HasValue)
        {
            query = query.Where(c => c.GroupId == groupId.Value);
        }
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var word = keyword.Trim();
            query = query.Where(c => c.Name.Contains(word, StringComparison.OrdinalIgnoreCase)
                || c.CommandText.Contains(word, StringComparison.OrdinalIgnoreCase)
                || (c.Description ?? string.Empty).Contains(word, StringComparison.OrdinalIgnoreCase));
        }

        var groupNames = (await groupStore.GetByTypeAsync(GroupBizType.Command)).ToDictionary(g => g.Id, g => g.Name);
        var items = query.Select(c => new CommandItemResponse(
            c.Id, c.Name, c.CommandText, c.ScriptType, c.Description, c.GroupId,
            c.GroupId.HasValue && groupNames.TryGetValue(c.GroupId.Value, out var name) ? name : null,
            c.IsPinned, c.SortOrder, c.TimeoutSeconds, c.LastExecTime, c.CreateTime, c.UpdateTime,
            c.DefaultTargetHostId));
        return Ok(items.ToList());
    }

    [HttpPost]
    public async Task<IResult> Create([FromBody] SaveCommandRequest request)
    {
        var (valid, message) = await ValidateAsync(request, excludeId: null);
        if (!valid)
        {
            return BadRequest(new MessageResponse(message));
        }

        var command = new LinuxCommand
        {
            Name = request.Name.Trim(),
            CommandText = request.CommandText.Trim(),
            ScriptType = (int)request.ScriptType,
            Description = request.Description?.Trim(),
            GroupId = request.GroupId,
            IsPinned = request.IsPinned,
            TimeoutSeconds = request.TimeoutSeconds,
            DefaultTargetHostId = string.IsNullOrWhiteSpace(request.DefaultTargetHostId) ? null : request.DefaultTargetHostId.Trim()
        };
        await commandStore.InsertAsync(command);
        await operationLogger.LogAsync(ScriptTypeLabel(request.ScriptType, "新增"), "指令", command.Name, Summarize(request), clientIp: HttpContext.GetClientIp());
        return Ok(command);
    }

    [HttpPut("{id:guid}")]
    public async Task<IResult> Update(Guid id, [FromBody] SaveCommandRequest request)
    {
        var command = await commandStore.GetByIdAsync(id);
        if (command is null)
        {
            return NotFound(new MessageResponse("指令不存在"));
        }

        var (valid, message) = await ValidateAsync(request, excludeId: id);
        if (!valid)
        {
            return BadRequest(new MessageResponse(message));
        }

        command.Name = request.Name.Trim();
        command.CommandText = request.CommandText.Trim();
        command.ScriptType = (int)request.ScriptType;
        command.Description = request.Description?.Trim();
        command.GroupId = request.GroupId;
        command.IsPinned = request.IsPinned;
        command.TimeoutSeconds = request.TimeoutSeconds;
        command.DefaultTargetHostId = string.IsNullOrWhiteSpace(request.DefaultTargetHostId) ? null : request.DefaultTargetHostId.Trim();
        await commandStore.UpdateAsync(command);
        await operationLogger.LogAsync(ScriptTypeLabel(request.ScriptType, "修改"), "指令", command.Name, Summarize(request), clientIp: HttpContext.GetClientIp());
        return Ok(command);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IResult> Delete(Guid id)
    {
        var command = await commandStore.GetByIdAsync(id);
        if (command is null)
        {
            return NotFound(new MessageResponse("指令不存在"));
        }

        await commandStore.DeleteAsync(id);
        await operationLogger.LogAsync("删除指令", "指令", command.Name, Summarize((ScriptType)command.ScriptType, command.CommandText, null), clientIp: HttpContext.GetClientIp());
        return Ok(new MessageResponse("已删除（历史执行记录保留）"));
    }

    /// <summary>执行已保存的指令并记录调用历史；支持指定目标主机与位置参数。</summary>
    [HttpPost("{id:guid}/Execute")]
    public async Task<IResult> Execute(Guid id, [FromBody] ExecuteCommandRequest? request = null)
    {
        var command = await commandStore.GetByIdAsync(id);
        if (command is null)
        {
            return NotFound(new MessageResponse("指令不存在"));
        }

        var targetHostId = !string.IsNullOrWhiteSpace(request?.TargetHostId)
            ? request.TargetHostId
            : command.DefaultTargetHostId;

        var target = !string.IsNullOrWhiteSpace(targetHostId)
            ? ExecutionTarget.Remote(targetHostId)
            : ExecutionTarget.Local;

        var triggerBy = User.Identity?.Name ?? "admin";
        var context = new CommandExecutionContext(
            CommandText: command.CommandText,
            ScriptType: (ScriptType)command.ScriptType,
            Arguments: request?.Arguments,
            TimeoutSeconds: command.TimeoutSeconds is null or <= 0 ? 60 : command.TimeoutSeconds.Value,
            Target: target,
            TriggerBy: triggerBy,
            CommandId: command.Id
        );

        var result = await executionEngine.ExecuteCommandAsync(context, command.Name, HttpContext.RequestAborted);

        var actionLabel = request?.Arguments is { Length: > 0 } ? "执行脚本" : "执行指令";
        await operationLogger.LogAsync(
            actionLabel,
            "指令", command.Name, Summarize((ScriptType)command.ScriptType, command.CommandText, request?.Arguments),
            success: result.Status == ExecutionStatus.Success,
            clientIp: HttpContext.GetClientIp());

        var response = new ExecuteResult
        {
            Success = result.Status == ExecutionStatus.Success,
            ExitCode = result.ExitCode,
            StandardOutput = result.Output,
            ErrorOutput = result.ErrorOutput,
            DurationMs = result.DurationMs,
            TimedOut = result.TimedOut,
            Truncated = result.Truncated,
            Status = result.Status,
            StartTime = result.StartTime,
            EndTime = result.EndTime,
            TargetType = (int)target.TargetType,
            TargetHostId = target.HostId,
            SkipReason = result.SkipReason
        };

        return Ok(response);
    }

    /// <summary>快速执行临时指令（不保存，支持指定本地或远程主机）。</summary>
    [HttpPost("QuickExecute")]
    public async Task<IResult> QuickExecute([FromBody] QuickExecuteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CommandText))
        {
            return BadRequest(new MessageResponse("指令内容不能为空"));
        }

        var target = !string.IsNullOrWhiteSpace(request.TargetHostId)
            ? ExecutionTarget.Remote(request.TargetHostId)
            : ExecutionTarget.Local;

        var triggerBy = User.Identity?.Name ?? "admin";
        var context = new CommandExecutionContext(
            CommandText: request.CommandText.Trim(),
            ScriptType: ScriptType.Command,
            Arguments: null,
            TimeoutSeconds: request.TimeoutSeconds is null or <= 0 ? 60 : request.TimeoutSeconds.Value,
            Target: target,
            TriggerBy: triggerBy
        );

        var result = await executionEngine.ExecuteCommandAsync(context, "(快速)", HttpContext.RequestAborted);

        await operationLogger.LogAsync(
            "快速执行指令",
            "指令", "(快速)", request.CommandText.Trim(),
            success: result.Status == ExecutionStatus.Success,
            clientIp: HttpContext.GetClientIp());

        var response = new ExecuteResult
        {
            Success = result.Status == ExecutionStatus.Success,
            ExitCode = result.ExitCode,
            StandardOutput = result.Output,
            ErrorOutput = result.ErrorOutput,
            DurationMs = result.DurationMs,
            TimedOut = result.TimedOut,
            Truncated = result.Truncated,
            Status = result.Status,
            StartTime = result.StartTime,
            EndTime = result.EndTime,
            TargetType = (int)target.TargetType,
            TargetHostId = target.HostId,
            SkipReason = result.SkipReason
        };

        return Ok(response);
    }

    /// <summary>批量多指令顺序或并发编排执行。</summary>
    [HttpPost("BatchExecute")]
    public async Task<IResult> BatchExecute([FromBody] BatchExecutionRequest request)
    {
        if (request.Commands is null || request.Commands.Count == 0)
        {
            return BadRequest(new MessageResponse("执行命令列表不能为空"));
        }

        var triggerBy = User.Identity?.Name ?? "admin";
        var batchResponse = await executionEngine.ExecuteBatchAsync(request, triggerBy, HttpContext.RequestAborted);

        await operationLogger.LogAsync(
            "批量执行指令",
            "指令",
            $"批量任务 ({request.Commands.Count} 条命令)",
            $"模式: {request.ExecutionMode}, 状态: {batchResponse.OverallStatus}, 耗时: {batchResponse.DurationMs}ms",
            success: batchResponse.OverallStatus == ExecutionStatus.Success,
            clientIp: HttpContext.GetClientIp());

        return Ok(batchResponse);
    }

    /// <summary>单条指令的执行历史。</summary>
    [HttpGet("{id:guid}/History")]
    public async Task<IResult> History(Guid id, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var result = await executionStore.QueryAsync(new ExecuteHistoryQuery
        {
            CommandId = id,
            Page = page,
            PageSize = pageSize,
        });
        return Ok(result);
    }

    private async Task<(bool Valid, string Message)> ValidateAsync(SaveCommandRequest request, Guid? excludeId)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return (false, "指令名称不能为空");
        }
        if (request.Name.Trim().Length > 100)
        {
            return (false, "指令名称不能超过 100 个字符");
        }
        if (string.IsNullOrWhiteSpace(request.CommandText))
        {
            return (false, "指令内容不能为空");
        }
        if (request.ScriptType == ScriptType.BashScript
            && System.Text.Encoding.UTF8.GetByteCount(request.CommandText) > 64 * 1024)
        {
            return (false, "脚本内容不能超过 64KB");
        }
        if (await commandStore.ExistsNameAsync(request.Name.Trim(), excludeId))
        {
            return (false, "指令名称已存在");
        }
        if (request.GroupId.HasValue && await groupStore.GetByIdAsync(request.GroupId.Value) is null)
        {
            return (false, "所属分组不存在");
        }
        if (request.TimeoutSeconds is < 1 or > 86400)
        {
            return (false, "超时时间必须在 1~86400 秒之间");
        }
        return (true, string.Empty);
    }

    private static string ScriptTypeLabel(ScriptType type, string action) =>
        type == ScriptType.BashScript ? action + "脚本" : action + "指令";

    private static string Summarize(SaveCommandRequest request) =>
        Summarize(request.ScriptType, request.CommandText, null);

    private static string Summarize(ScriptType type, string text, string? arguments)
    {
        if (type != ScriptType.BashScript)
        {
            return text;
        }
        var summary = $"[脚本 {text.Length} 字符] " + text.Replace("\r", " ").Replace("\n", " ").Trim();
        if (summary.Length > 200)
        {
            summary = summary[..200] + "…";
        }
        if (arguments is { Length: > 0 })
        {
            summary += $" | 参数: {arguments}";
        }
        return summary;
    }
}
