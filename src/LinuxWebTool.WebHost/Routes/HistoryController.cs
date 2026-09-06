using LinuxWebTool.WebHost.Extensions;

namespace LinuxWebTool.WebHost.Routes;

/// <summary>执行历史：调用历史（手动/快速/定时）的分页检索与清理。</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class HistoryController(ExecutionStore executionStore, IOperationLogger operationLogger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] ExecuteHistoryQuery query)
    {
        var result = await executionStore.QueryAsync(query);
        return Ok(result);
    }

    [HttpDelete]
    public async Task<IActionResult> Clear([FromQuery] int? olderThanDays)
    {
        if (olderThanDays is < 1)
        {
            return BadRequest(new { message = "olderThanDays 必须大于 0" });
        }

        await executionStore.ClearAsync(olderThanDays);
        var detail = olderThanDays.HasValue ? $"清理 {DateTime.Today.AddDays(-olderThanDays.Value):yyyy-MM-dd} 之前的记录" : "清空全部记录";
        await operationLogger.LogAsync("清理执行历史", "执行历史", string.Empty, detail, clientIp: HttpContext.GetClientIp());
        return Ok(new { message = detail + " 完成" });
    }
}
