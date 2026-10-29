using LinuxWebTool.Contracts.Features.Commands.Contracts;
using LinuxWebTool.Contracts.Features.Workflows.Contracts;
using LinuxWebTool.Contracts.Shared.Contracts;
using LinuxWebTool.Infrastructure.Features.Scheduling.Adapters;
using LinuxWebTool.Infrastructure.Shared.Persistence;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using LinuxWebTool.Infrastructure.Shared.Support;

namespace LinuxWebTool.WebHost.Features.Workflows.Routes;

/// <summary>工作流控制器：支持多活动（Shell/HTTP）串行与并行管道编排、即时运行与执行追踪。</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class WorkflowsController(
    WorkflowStore workflowStore,
    GroupStore groupStore,
    ExecutionStore executionStore,
    IUnifiedExecutionEngine executionEngine,
    IOperationLogger operationLogger) : MinimalApi.ControllerBase
{
    [HttpGet]
    public async Task<IResult> GetAll([FromQuery] Guid? groupId, [FromQuery] string? keyword)
    {
        var workflows = await workflowStore.GetAllAsync();
        IEnumerable<WorkflowEntity> query = workflows;
        if (groupId.HasValue)
        {
            query = query.Where(w => w.GroupId == groupId.Value);
        }
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var word = keyword.Trim();
            query = query.Where(w => w.Name.Contains(word, StringComparison.OrdinalIgnoreCase)
                || (w.Description ?? string.Empty).Contains(word, StringComparison.OrdinalIgnoreCase));
        }

        var groupNames = (await groupStore.GetByTypeAsync(GroupBizType.Command)).ToDictionary(g => g.Id, g => g.Name);
        var items = query.Select(w => new WorkflowItemResponse(
            w.Id, w.Name, w.Description, w.ExecutionMode, w.FailurePolicy, 
            w.DefaultTargetHostId, w.DefinitionJson, w.Enabled, w.GroupId,
            w.GroupId.HasValue && groupNames.TryGetValue(w.GroupId.Value, out var gName) ? gName : null,
            w.IsPinned, w.SortOrder, w.LastRunTime, w.CreateTime, w.UpdateTime
        ));

        return Ok(items.ToList());
    }

    [HttpGet("{id:guid}")]
    public async Task<IResult> GetById(Guid id)
    {
        var entity = await workflowStore.GetByIdAsync(id);
        if (entity is null)
        {
            return NotFound(new MessageResponse("工作流不存在"));
        }

        var response = new WorkflowItemResponse(
            entity.Id, entity.Name, entity.Description, entity.ExecutionMode, entity.FailurePolicy,
            entity.DefaultTargetHostId, entity.DefinitionJson, entity.Enabled, entity.GroupId, null,
            entity.IsPinned, entity.SortOrder, entity.LastRunTime, entity.CreateTime, entity.UpdateTime
        );

        return Ok(response);
    }

    [HttpPost]
    public async Task<IResult> Create([FromBody] SaveWorkflowRequest request)
    {
        var (valid, message) = await ValidateAsync(request, excludeId: null);
        if (!valid)
        {
            return BadRequest(new MessageResponse(message));
        }

        var entity = new WorkflowEntity
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Description = request.Description?.Trim(),
            ExecutionMode = (int)request.ExecutionMode,
            FailurePolicy = (int)request.FailurePolicy,
            DefaultTargetHostId = string.IsNullOrWhiteSpace(request.DefaultTargetHostId) ? null : request.DefaultTargetHostId.Trim(),
            DefinitionJson = request.DefinitionJson.Trim(),
            Enabled = request.Enabled,
            GroupId = request.GroupId,
            IsPinned = request.IsPinned,
            SortOrder = request.SortOrder
        };

        await workflowStore.InsertAsync(entity);
        await operationLogger.LogAsync("新增工作流", "工作流", entity.Name, entity.Description ?? string.Empty, clientIp: HttpContext.GetClientIp());
        return Ok(entity);
    }

    [HttpPut("{id:guid}")]
    public async Task<IResult> Update(Guid id, [FromBody] SaveWorkflowRequest request)
    {
        var entity = await workflowStore.GetByIdAsync(id);
        if (entity is null)
        {
            return NotFound(new MessageResponse("工作流不存在"));
        }

        var (valid, message) = await ValidateAsync(request, excludeId: id);
        if (!valid)
        {
            return BadRequest(new MessageResponse(message));
        }

        entity.Name = request.Name.Trim();
        entity.Description = request.Description?.Trim();
        entity.ExecutionMode = (int)request.ExecutionMode;
        entity.FailurePolicy = (int)request.FailurePolicy;
        entity.DefaultTargetHostId = string.IsNullOrWhiteSpace(request.DefaultTargetHostId) ? null : request.DefaultTargetHostId.Trim();
        entity.DefinitionJson = request.DefinitionJson.Trim();
        entity.Enabled = request.Enabled;
        entity.GroupId = request.GroupId;
        entity.IsPinned = request.IsPinned;
        entity.SortOrder = request.SortOrder;

        await workflowStore.UpdateAsync(entity);
        await operationLogger.LogAsync("修改工作流", "工作流", entity.Name, entity.Description ?? string.Empty, clientIp: HttpContext.GetClientIp());
        return Ok(entity);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IResult> Delete(Guid id)
    {
        var entity = await workflowStore.GetByIdAsync(id);
        if (entity is null)
        {
            return NotFound(new MessageResponse("工作流不存在"));
        }

        await workflowStore.DeleteAsync(id);
        await operationLogger.LogAsync("删除工作流", "工作流", entity.Name, string.Empty, clientIp: HttpContext.GetClientIp());
        return Ok(new MessageResponse("已删除"));
    }

    [HttpPost("{id:guid}/Toggle")]
    public async Task<IResult> Toggle(Guid id)
    {
        var entity = await workflowStore.GetByIdAsync(id);
        if (entity is null)
        {
            return NotFound(new MessageResponse("工作流不存在"));
        }

        entity.Enabled = !entity.Enabled;
        await workflowStore.ToggleEnabledAsync(id, entity.Enabled);
        await operationLogger.LogAsync(entity.Enabled ? "启用工作流" : "停用工作流", "工作流", entity.Name, string.Empty, clientIp: HttpContext.GetClientIp());
        return Ok(new ToggleWorkflowResponse(entity.Enabled));
    }

    [HttpPost("{id:guid}/Run")]
    public async Task<IResult> Run(Guid id, [FromBody] RunWorkflowRequest? request = null)
    {
        var entity = await workflowStore.GetByIdAsync(id);
        if (entity is null)
        {
            return NotFound(new MessageResponse("工作流不存在"));
        }

        var triggerBy = User.Identity?.Name ?? "admin";
        ExecutionTarget? targetOverride = !string.IsNullOrWhiteSpace(request?.TargetHostId)
            ? ExecutionTarget.Remote(request.TargetHostId)
            : null;

        var result = await executionEngine.ExecuteWorkflowAsync(id, triggerBy, targetOverride, HttpContext.RequestAborted);

        await operationLogger.LogAsync(
            "运行工作流", "工作流", entity.Name,
            $"BatchId: {result.BatchId}, 状态: {result.OverallStatus}, 步骤数: {result.Results.Count}",
            success: result.OverallStatus == ExecutionStatus.Success,
            clientIp: HttpContext.GetClientIp());

        return Ok(result);
    }

    [HttpGet("{id:guid}/History")]
    public async Task<IResult> History(Guid id, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var result = await executionStore.QueryAsync(new ExecuteHistoryQuery
        {
            WorkflowId = id.ToString(),
            Page = page,
            PageSize = pageSize
        });

        return Ok(result);
    }

    private async Task<(bool Valid, string Message)> ValidateAsync(SaveWorkflowRequest request, Guid? excludeId)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return (false, "工作流名称不能为空");
        }
        if (request.Name.Trim().Length > 100)
        {
            return (false, "工作流名称不能超过 100 个字符");
        }
        if (string.IsNullOrWhiteSpace(request.DefinitionJson))
        {
            return (false, "工作流编排定义不能为空");
        }
        if (await workflowStore.ExistsNameAsync(request.Name.Trim(), excludeId))
        {
            return (false, "工作流名称已存在");
        }
        if (request.GroupId.HasValue && await groupStore.GetByIdAsync(request.GroupId.Value) is null)
        {
            return (false, "所属分组不存在");
        }
        return (true, string.Empty);
    }
}
