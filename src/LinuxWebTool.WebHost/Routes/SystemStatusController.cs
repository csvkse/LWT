using LinuxWebTool.Contracts.Interfaces;

namespace LinuxWebTool.WebHost.Routes;

/// <summary>系统状态：即时全量采集 + 历史曲线序列（整机 / 磁盘 / 网络 / 进程）。</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SystemStatusController(
    ISystemStatusProvider statusProvider,
    SystemStatusStore statusStore,
    SystemStatusDiskStore diskStore,
    SystemStatusNetStore netStore,
    SystemStatusProcessStore processStore) : MinimalApi.ControllerBase
{
    /// <summary>即时全量状态（主机 / CPU / 内存 / 磁盘明细 / 网卡速率 / 进程 Top）。</summary>
    [HttpGet]
    public async Task<IResult> Get(CancellationToken cancellationToken)
    {
        var status = await statusProvider.GetStatusAsync(cancellationToken);
        return Ok(status);
    }

    /// <summary>历史曲线序列（hours 上限 720 = 30 天）。</summary>
    [HttpGet("History")]
    public async Task<IResult> History([FromQuery] int hours = 6)
    {
        var points = await statusStore.QueryAsync(hours);
        return Ok(points);
    }

    /// <summary>磁盘挂载点历史曲线序列（hours 上限 720 = 30 天）。</summary>
    [HttpGet("DiskHistory")]
    public async Task<IResult> DiskHistory([FromQuery] int hours = 6)
    {
        var points = await diskStore.QueryAsync(hours);
        return Ok(points);
    }

    /// <summary>网卡历史曲线序列（hours 上限 720 = 30 天）。</summary>
    [HttpGet("NetHistory")]
    public async Task<IResult> NetHistory([FromQuery] int hours = 6)
    {
        var points = await netStore.QueryAsync(hours);
        return Ok(points);
    }

    /// <summary>进程序列（hours 上限 720 = 30 天）。</summary>
    [HttpGet("ProcessHistory")]
    public async Task<IResult> ProcessHistory([FromQuery] int hours = 6)
    {
        var points = await processStore.QueryAsync(hours);
        return Ok(points);
    }

    /// <summary>聚合历史：一次性返回整机 / 磁盘 / 网络 / 进程四类序列（共用时间轴，前端联动渲染）。</summary>
    [HttpGet("ResourceHistory")]
    public async Task<IResult> ResourceHistory([FromQuery] int hours = 6)
    {
        return Ok(new ResourceHistoryResponse(
            await statusStore.QueryAsync(hours),
            await diskStore.QueryAsync(hours),
            await netStore.QueryAsync(hours),
            await processStore.QueryAsync(hours)));
    }
}
