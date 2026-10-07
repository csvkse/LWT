using LinuxWebTool.WebHost.Composition;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
namespace LinuxWebTool.WebHost.Features.EasyTier.Routes;

/// <summary>
/// EasyTier 虚拟组网控制器（节点生命周期、实时拓扑、配置热打补丁与内核热升级）
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class EasyTierController(
    IEasyTierManager manager,
    IOperationLogger operationLogger) : MinimalApi.ControllerBase
{
    [HttpGet("Nodes")]
    public async Task<IResult> GetNodes()
    {
        var nodes = await manager.GetAllNodeStatusesAsync();
        return Ok(nodes);
    }

    [HttpGet("AvailablePort")]
    public async Task<IResult> GetAvailablePort([FromQuery] int? startPort)
    {
        var result = await manager.GetNextAvailablePortAsync(startPort, HttpContext.RequestAborted);
        return Ok(result);
    }

    [HttpGet("Nodes/{id}")]
    public async Task<IResult> GetNodeDetail(string id)
    {
        var detail = await manager.GetNodeDetailAsync(id);
        if (detail == null) return NotFound(new MessageResponse("未找到指定的 EasyTier 节点"));
        return Ok(detail);
    }

    [HttpPost("Nodes")]
    public async Task<IResult> CreateNode([FromBody] CreateEasyTierNodeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.InstanceName))
        {
            return BadRequest(new MessageResponse("节点实例名称不能为空"));
        }
        if (string.IsNullOrWhiteSpace(request.NetworkName))
        {
            return BadRequest(new MessageResponse("虚拟网络名称不能为空"));
        }

        try
        {
            var created = await manager.CreateNodeAsync(request);
            await operationLogger.LogAsync("新建 EasyTier 节点", "虚拟网络", created.InstanceName, created.NetworkName, clientIp: HttpContext.GetClientIp());
            return Ok(created);
        }
        catch (Exception ex)
        {
            return BadRequest(new MessageResponse(ex.Message));
        }
    }

    [HttpPut("Nodes/{id}")]
    public async Task<IResult> UpdateNode(string id, [FromBody] UpdateEasyTierNodeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.InstanceName))
        {
            return BadRequest(new MessageResponse("节点实例名称不能为空"));
        }
        if (string.IsNullOrWhiteSpace(request.NetworkName))
        {
            return BadRequest(new MessageResponse("虚拟网络名称不能为空"));
        }

        try
        {
            var updated = await manager.UpdateNodeAsync(id, request);
            await operationLogger.LogAsync("更新 EasyTier 节点配置", "虚拟网络", updated.InstanceName, updated.NetworkName, clientIp: HttpContext.GetClientIp());
            return Ok(updated);
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new MessageResponse("未找到指定的 EasyTier 节点"));
        }
        catch (Exception ex)
        {
            return BadRequest(new MessageResponse(ex.Message));
        }
    }

    [HttpPost("Nodes/{id}/Config")]
    public async Task<IResult> PatchNodeConfig(string id, [FromBody] EasyTierPatchRequestDto patch)
    {
        var result = await manager.PatchNodeConfigAsync(id, patch);
        if (!result.Success)
        {
            return BadRequest(new MessageResponse(result.Message));
        }

        await operationLogger.LogAsync("运行时热补丁 EasyTier 节点", "虚拟网络", id, result.Message, clientIp: HttpContext.GetClientIp());
        return Ok(result);
    }

    [HttpDelete("Nodes/{id}")]
    public async Task<IResult> DeleteNode(string id)
    {
        var detail = await manager.GetNodeDetailAsync(id);
        var name = detail?.Config.InstanceName ?? id;

        var deleted = await manager.DeleteNodeAsync(id);
        if (!deleted) return NotFound(new MessageResponse("未找到指定的 EasyTier 节点"));

        await operationLogger.LogAsync("删除 EasyTier 节点", "虚拟网络", name, id, clientIp: HttpContext.GetClientIp());
        return Ok(new MessageResponse("节点已成功删除"));
    }

    [HttpPost("Nodes/{id}/Start")]
    public async Task<IResult> StartNode(string id)
    {
        try
        {
            var success = await manager.StartNodeAsync(id);
            if (!success) return BadRequest(new MessageResponse("启动节点失败"));

            await operationLogger.LogAsync("启动 EasyTier 节点", "虚拟网络", id, "启动成功", clientIp: HttpContext.GetClientIp());
            return Ok(new MessageResponse("节点已成功启动"));
        }
        catch (Exception ex)
        {
            return BadRequest(new MessageResponse(ex.Message));
        }
    }

    [HttpPost("Nodes/{id}/Stop")]
    public async Task<IResult> StopNode(string id)
    {
        try
        {
            var success = await manager.StopNodeAsync(id);
            if (!success) return BadRequest(new MessageResponse("停止节点失败"));

            await operationLogger.LogAsync("停止 EasyTier 节点", "虚拟网络", id, "停止成功", clientIp: HttpContext.GetClientIp());
            return Ok(new MessageResponse("节点已停止运行"));
        }
        catch (Exception ex)
        {
            return BadRequest(new MessageResponse(ex.Message));
        }
    }

    [HttpGet("Engine/Status")]
    public async Task<IResult> GetEngineStatus()
    {
        var status = await manager.GetEngineStatusAsync();
        return Ok(status);
    }

    [HttpPost("Engine/Upgrade")]
    public async Task<IResult> UpgradeEngine()
    {
        if (!HttpContext.Request.HasFormContentType)
        {
            return BadRequest(new MessageResponse("请以 multipart/form-data 格式上传内核文件"));
        }

        var form = await HttpContext.Request.ReadFormAsync();
        var file = form.Files.FirstOrDefault();
        if (file == null || file.Length == 0)
        {
            return BadRequest(new MessageResponse("未检测到上传的二进制内核文件"));
        }

        try
        {
            await using var stream = file.OpenReadStream();
            var result = await manager.UpgradeEngineAsync(stream, file.FileName);
            await operationLogger.LogAsync("升级 EasyTier 内核引擎", "系统运维", result.NewVersion, result.Message, clientIp: HttpContext.GetClientIp());
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new MessageResponse($"升级失败: {ex.Message}"));
        }
    }

    [HttpGet("Engine/Releases")]
    public async Task<IResult> GetGitHubReleases([FromQuery] string? proxyPrefix)
    {
        try
        {
            var info = await manager.CheckGitHubReleaseAsync(proxyPrefix);
            return Ok(info);
        }
        catch (Exception ex)
        {
            return BadRequest(new MessageResponse($"获取 GitHub 发布信息失败: {ex.Message}"));
        }
    }

    [HttpPost("Engine/InstallGitHub")]
    public async Task<IResult> InstallGitHubRelease([FromBody] InstallGitHubReleaseRequest request)
    {
        try
        {
            var result = await manager.InstallGitHubReleaseAsync(request);
            await operationLogger.LogAsync("从 GitHub 安装 EasyTier 内核", "系统运维", result.NewVersion, result.Message, clientIp: HttpContext.GetClientIp());
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new MessageResponse($"从 GitHub 下载安装内核失败: {ex.Message}"));
        }
    }
}
