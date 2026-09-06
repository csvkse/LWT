using LinuxWebTool.Contracts.Interfaces;

namespace LinuxWebTool.WebHost.Routes;

/// <summary>系统状态：即时全量采集 + 历史曲线序列。</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SystemStatusController(ISystemStatusProvider statusProvider, SystemStatusStore statusStore) : ControllerBase
{
    /// <summary>即时全量状态（主机 / CPU / 内存 / 磁盘明细 / 网卡速率 / 进程 Top）。</summary>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var status = await statusProvider.GetStatusAsync(cancellationToken);
        return Ok(status);
    }

    /// <summary>历史曲线序列（hours 上限 720 = 30 天）。</summary>
    [HttpGet("History")]
    public async Task<IActionResult> History([FromQuery] int hours = 6)
    {
        var points = await statusStore.QueryAsync(hours);
        return Ok(points);
    }
}
