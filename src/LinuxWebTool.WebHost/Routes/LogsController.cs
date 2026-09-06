using LinuxWebTool.Infrastructure.Logging;
using LinuxWebTool.WebHost.Extensions;

namespace LinuxWebTool.WebHost.Routes;

/// <summary>日志查看：操作日志（DB）与程序/调试日志文件（tail）。</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class LogsController(OperationLogStore operationLogStore, LogFileService logFileService) : ControllerBase
{
    /// <summary>操作日志分页查询。</summary>
    [HttpGet("Operations")]
    public async Task<IActionResult> Operations([FromQuery] OperationLogQuery query)
    {
        var result = await operationLogStore.QueryAsync(query);
        return Ok(result);
    }

    /// <summary>日志文件列表（app-* 程序日志、debug-* 调试日志）。</summary>
    [HttpGet("Files")]
    public IActionResult Files()
    {
        return Ok(logFileService.List());
    }

    /// <summary>查看日志文件末尾 N 行（默认 300）。</summary>
    [HttpGet("Files/{name}")]
    public IActionResult FileContent(string name, [FromQuery] int tail = 300)
    {
        var content = logFileService.ReadTail(name, tail);
        if (content is null)
        {
            return NotFound(new { message = "日志文件不存在或文件名非法" });
        }
        return Ok(new { name, tail, content });
    }
}
