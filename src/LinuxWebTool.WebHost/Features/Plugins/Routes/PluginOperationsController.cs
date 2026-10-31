using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.WebHost.Composition;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LinuxWebTool.WebHost.Features.Plugins.Routes;

/// <summary>
/// 插件异步操作与产物管理控制器。
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PluginOperationsController(
    IPluginOperationCoordinator coordinator,
    IPluginArtifactStore artifactStore) : MinimalApi.ControllerBase
{
    [HttpPost("Submit")]
    public async Task<IResult> Submit([FromBody] CommandInvocation invocation, CancellationToken ct)
    {
        var caller = User.Identity?.Name ?? "web";
        var callWithUser = invocation with { Caller = caller };

        try
        {
            var accepted = await coordinator.SubmitAsync(callWithUser, ct);
            return Accepted(accepted);
        }
        catch (Exception ex)
        {
            return BadRequest(new MessageResponse(ex.Message));
        }
    }

    [HttpGet("{operationId}")]
    public async Task<IResult> GetById(string operationId, CancellationToken ct)
    {
        var caller = User.Identity?.Name ?? "web";
        var op = await coordinator.GetAsync(operationId, caller, ct);
        return op != null ? Ok(op) : NotFound(new MessageResponse($"未找到操作或无权查看: '{operationId}'"));
    }

    [HttpPost("{operationId}/Cancel")]
    public async Task<IResult> Cancel(string operationId, CancellationToken ct)
    {
        var caller = User.Identity?.Name ?? "web";
        try
        {
            var op = await coordinator.CancelAsync(operationId, caller, ct);
            return op != null ? Ok(op) : NotFound(new MessageResponse($"未找到操作: '{operationId}'"));
        }
        catch (Exception ex)
        {
            return BadRequest(new MessageResponse(ex.Message));
        }
    }

    [HttpGet("{operationId}/Artifacts/{artifactId}")]
    public async Task<IResult> DownloadArtifact(string operationId, string artifactId, CancellationToken ct)
    {
        var caller = User.Identity?.Name ?? "web";
        var meta = await artifactStore.GetArtifactMetaAsync(operationId, artifactId, caller, ct);
        if (meta == null)
        {
            return NotFound(new MessageResponse($"未找到产物: '{artifactId}'"));
        }

        var stream = await artifactStore.OpenArtifactReadStreamAsync(operationId, artifactId, caller, ct);
        if (stream == null)
        {
            return NotFound(new MessageResponse($"产物文件不存在或已过期清理: '{artifactId}'"));
        }

        return File(stream, meta.ContentType ?? "application/octet-stream", meta.Filename);
    }
}
