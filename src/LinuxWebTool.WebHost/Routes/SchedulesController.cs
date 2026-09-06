using LinuxWebTool.WebHost.Extensions;
using Quartz;

namespace LinuxWebTool.WebHost.Routes;

/// <summary>定时任务：CRUD、启停、立即执行、执行记录；增删改实时同步 Quartz 内存调度。</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SchedulesController(
    ScheduleStore scheduleStore,
    CommandStore commandStore,
    GroupStore groupStore,
    ExecutionStore executionStore,
    IScheduleManager scheduleManager,
    IOperationLogger operationLogger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var tasks = await scheduleStore.GetAllAsync();
        var commands = (await commandStore.GetAllAsync()).ToDictionary(c => c.Id);
        var groupNames = (await groupStore.GetByTypeAsync(GroupBizType.Schedule)).ToDictionary(g => g.Id, g => g.Name);
        var items = tasks.Select(t =>
        {
            commands.TryGetValue(t.CommandId, out var command);
            return new
            {
                t.Id,
                t.Name,
                t.CommandId,
                CommandName = command?.Name ?? "(指令已删除)",
                CommandText = command?.CommandText ?? string.Empty,
                ScriptType = command?.ScriptType ?? 0,
                t.CronExpression,
                t.Enabled,
                t.GroupId,
                GroupName = t.GroupId.HasValue && groupNames.TryGetValue(t.GroupId.Value, out var name) ? name : null,
                t.IsPinned,
                t.SortOrder,
                t.TimeoutSeconds,
                t.Arguments,
                t.LastRunTime,
                t.NextRunTime,
                t.CreateTime,
                t.UpdateTime,
            };
        });
        return Ok(items);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveScheduleRequest request)
    {
        var (valid, message, cron) = await ValidateAsync(request);
        if (!valid)
        {
            return BadRequest(new { message });
        }

        var task = new ScheduleTask
        {
            Name = request.Name.Trim(),
            CommandId = request.CommandId,
            CronExpression = cron,
            Enabled = request.Enabled,
            GroupId = request.GroupId,
            IsPinned = request.IsPinned,
            TimeoutSeconds = request.TimeoutSeconds,
            Arguments = string.IsNullOrWhiteSpace(request.Arguments) ? null : request.Arguments.Trim(),
        };
        await scheduleStore.InsertAsync(task);
        task.NextRunTime = await SyncScheduleAsync(task);
        await operationLogger.LogAsync("新增定时任务", "定时任务", task.Name, task.CronExpression, clientIp: HttpContext.GetClientIp());
        return Ok(task);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] SaveScheduleRequest request)
    {
        var task = await scheduleStore.GetByIdAsync(id);
        if (task is null)
        {
            return NotFound(new { message = "定时任务不存在" });
        }

        var (valid, message, cron) = await ValidateAsync(request);
        if (!valid)
        {
            return BadRequest(new { message });
        }

        task.Name = request.Name.Trim();
        task.CommandId = request.CommandId;
        task.CronExpression = cron;
        task.Enabled = request.Enabled;
        task.GroupId = request.GroupId;
        task.IsPinned = request.IsPinned;
        task.TimeoutSeconds = request.TimeoutSeconds;
        task.Arguments = string.IsNullOrWhiteSpace(request.Arguments) ? null : request.Arguments.Trim();
        await scheduleStore.UpdateAsync(task);
        task.NextRunTime = await SyncScheduleAsync(task);
        await operationLogger.LogAsync("修改定时任务", "定时任务", task.Name, task.CronExpression, clientIp: HttpContext.GetClientIp());
        return Ok(task);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var task = await scheduleStore.GetByIdAsync(id);
        if (task is null)
        {
            return NotFound(new { message = "定时任务不存在" });
        }

        await scheduleManager.RemoveAsync(id);
        await scheduleStore.DeleteAsync(id);
        await operationLogger.LogAsync("删除定时任务", "定时任务", task.Name, task.CronExpression, clientIp: HttpContext.GetClientIp());
        return Ok(new { message = "已删除" });
    }

    /// <summary>启用 / 停用切换：停用即从调度器移除触发器。</summary>
    [HttpPost("{id:guid}/Toggle")]
    public async Task<IActionResult> Toggle(Guid id)
    {
        var task = await scheduleStore.GetByIdAsync(id);
        if (task is null)
        {
            return NotFound(new { message = "定时任务不存在" });
        }

        task.Enabled = !task.Enabled;
        await scheduleStore.UpdateAsync(task);
        task.NextRunTime = await SyncScheduleAsync(task);
        await operationLogger.LogAsync(task.Enabled ? "启用定时任务" : "停用定时任务", "定时任务", task.Name, task.CronExpression, clientIp: HttpContext.GetClientIp());
        return Ok(new { task.Enabled, task.NextRunTime });
    }

    /// <summary>立即执行一次（不影响 Cron 计划）。</summary>
    [HttpPost("{id:guid}/RunNow")]
    public async Task<IActionResult> RunNow(Guid id)
    {
        var task = await scheduleStore.GetByIdAsync(id);
        if (task is null)
        {
            return NotFound(new { message = "定时任务不存在" });
        }

        await scheduleManager.TriggerNowAsync(task);
        await operationLogger.LogAsync("立即运行定时任务", "定时任务", task.Name, task.CronExpression, clientIp: HttpContext.GetClientIp());
        return Ok(new { message = "已触发，执行结果请在执行历史中查看" });
    }

    /// <summary>定时任务的执行记录。</summary>
    [HttpGet("{id:guid}/Records")]
    public async Task<IActionResult> Records(Guid id, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var result = await executionStore.QueryAsync(new ExecuteHistoryQuery
        {
            ScheduleTaskId = id,
            Page = page,
            PageSize = pageSize,
        });
        return Ok(result);
    }

    private async Task<(bool Valid, string Message, string Cron)> ValidateAsync(SaveScheduleRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return (false, "任务名称不能为空", string.Empty);
        }
        if (request.Name.Trim().Length > 100)
        {
            return (false, "任务名称不能超过 100 个字符", string.Empty);
        }
        if (await commandStore.GetByIdAsync(request.CommandId) is null)
        {
            return (false, "引用的指令不存在", string.Empty);
        }
        if (!CronHelper.TryNormalize(request.CronExpression, out var cron, out var cronError))
        {
            return (false, cronError, string.Empty);
        }
        if (!CronExpression.IsValidExpression(cron))
        {
            return (false, $"无效的 Cron 表达式：{request.CronExpression}", string.Empty);
        }
        if (request.GroupId.HasValue && await groupStore.GetByIdAsync(request.GroupId.Value) is null)
        {
            return (false, "所属分组不存在", string.Empty);
        }
        if (request.TimeoutSeconds is < 1 or > 86400)
        {
            return (false, "超时时间必须在 1~86400 秒之间", string.Empty);
        }
        if (request.Arguments is { Length: > 500 })
        {
            return (false, "位置参数不能超过 500 个字符", string.Empty);
        }
        return (true, string.Empty, cron);
    }

    /// <summary>同步 Quartz 调度并回写下次执行时间（停用时置空）。</summary>
    private async Task<DateTime?> SyncScheduleAsync(ScheduleTask task)
    {
        try
        {
            var nextRun = await scheduleManager.SyncAsync(task);
            task.NextRunTime = nextRun;
            await scheduleStore.UpdateAsync(task);
            return nextRun;
        }
        catch (InvalidOperationException)
        {
            throw; // 无效 Cron：控制器层已校验，防御性保留
        }
    }
}
