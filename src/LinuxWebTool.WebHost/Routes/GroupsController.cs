using LinuxWebTool.WebHost.Extensions;

namespace LinuxWebTool.WebHost.Routes;

/// <summary>分组管理：指令分组与定时任务分组共用一套 CRUD，按 bizType 区分。</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class GroupsController(
    GroupStore groupStore,
    CommandStore commandStore,
    ScheduleStore scheduleStore,
    IOperationLogger operationLogger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] GroupBizType bizType = GroupBizType.Command)
    {
        var groups = (await groupStore.GetByTypeAsync(bizType)).ToList();
        var items = new List<object>(groups.Count);
        foreach (var group in groups)
        {
            var usage = bizType == GroupBizType.Command
                ? await commandStore.CountByGroupAsync(group.Id)
                : await scheduleStore.CountByGroupAsync(group.Id);
            items.Add(new
            {
                group.Id,
                group.Name,
                group.SortOrder,
                group.CreateTime,
                UsageCount = usage,
            });
        }
        return Ok(items);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveGroupRequest request)
    {
        var (valid, message) = await ValidateAsync(request, excludeId: null);
        if (!valid)
        {
            return BadRequest(new MessageResponse(message));
        }

        var group = new CommandGroup
        {
            Name = request.Name.Trim(),
            BizType = (int)request.BizType,
            SortOrder = request.SortOrder,
        };
        await groupStore.InsertAsync(group);
        await operationLogger.LogAsync("新增分组", "分组", group.Name, $"类型：{request.BizType}", clientIp: HttpContext.GetClientIp());
        return Ok(group);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] SaveGroupRequest request)
    {
        var group = await groupStore.GetByIdAsync(id);
        if (group is null)
        {
            return NotFound(new MessageResponse("分组不存在"));
        }

        var (valid, message) = await ValidateAsync(request, excludeId: id);
        if (!valid)
        {
            return BadRequest(new MessageResponse(message));
        }

        group.Name = request.Name.Trim();
        group.SortOrder = request.SortOrder;
        await groupStore.UpdateAsync(group);
        await operationLogger.LogAsync("修改分组", "分组", group.Name, clientIp: HttpContext.GetClientIp());
        return Ok(group);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var group = await groupStore.GetByIdAsync(id);
        if (group is null)
        {
            return NotFound(new MessageResponse("分组不存在"));
        }

        var usage = group.BizType == (int)GroupBizType.Command
            ? await commandStore.CountByGroupAsync(id)
            : await scheduleStore.CountByGroupAsync(id);
        if (usage > 0)
        {
            return BadRequest(new MessageResponse($"分组下仍有 {usage} 个条目，请先移出后再删除"));
        }

        await groupStore.DeleteAsync(id);
        await operationLogger.LogAsync("删除分组", "分组", group.Name, clientIp: HttpContext.GetClientIp());
        return Ok(new MessageResponse("已删除"));
    }

    private async Task<(bool Valid, string Message)> ValidateAsync(SaveGroupRequest request, Guid? excludeId)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return (false, "分组名称不能为空");
        }
        if (request.Name.Trim().Length > 100)
        {
            return (false, "分组名称不能超过 100 个字符");
        }
        if (await groupStore.ExistsNameAsync(request.Name.Trim(), request.BizType, excludeId))
        {
            return (false, "同类型下分组名称已存在");
        }
        return (true, string.Empty);
    }
}
