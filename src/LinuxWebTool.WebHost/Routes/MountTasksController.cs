using LinuxWebTool.Infrastructure.Mount;

namespace LinuxWebTool.WebHost.Routes;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class MountTasksController(MountStateMachineService scheduler) : MinimalApi.ControllerBase
{
    [HttpGet("{id:guid}")]
    public IResult Get(Guid id) => scheduler.GetTask(id) is { } task ? Ok(task)
        : NotFound(new MessageResponse("挂载任务不存在或已过期"));
}
