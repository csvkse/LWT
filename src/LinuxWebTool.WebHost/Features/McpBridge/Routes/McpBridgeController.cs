using LinuxWebTool.Contracts.Features.McpBridge.Contracts;
using LinuxWebTool.WebHost.Composition;
using LinuxWebTool.WebHost.MinimalApi;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace LinuxWebTool.WebHost.Features.McpBridge.Routes;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class McpBridgeController(IMcpBridgeOrchestrator orchestrator) : MinimalApi.ControllerBase
{
    [HttpGet("Status")]
    public async Task<IResult> GetStatus(CancellationToken ct)
    {
        var status = await orchestrator.GetStatusAsync(ct);
        return Ok(status);
    }

    [HttpPut("Tunnel")]
    public async Task<IResult> SaveTunnel([FromBody] SaveTunnelRequest request, CancellationToken ct)
    {
        var result = await orchestrator.SaveTunnelAsync(request, ct);
        return result.Success ? Ok(result) : BadRequest(new MessageResponse(result.Message ?? "保存 Tunnel 配置失败"));
    }

    [HttpPost("Tunnel/Start")]
    public async Task<IResult> StartTunnel(CancellationToken ct)
    {
        var result = await orchestrator.StartTunnelAsync(ct);
        return result.Success ? Ok(result) : BadRequest(new MessageResponse(result.Message ?? "启动 Tunnel 失败"));
    }

    [HttpPost("Tunnel/Stop")]
    public async Task<IResult> StopTunnel(CancellationToken ct)
    {
        var result = await orchestrator.StopTunnelAsync(ct);
        return result.Success ? Ok(result) : BadRequest(new MessageResponse(result.Message ?? "停止 Tunnel 失败"));
    }

    [HttpGet("Servers")]
    public async Task<IResult> GetServers(CancellationToken ct)
    {
        var result = await orchestrator.GetServersAsync(ct);
        return Ok(result);
    }

    [HttpPut("Servers/{id}")]
    public async Task<IResult> SaveServer(string id, [FromBody] SaveServerRequest request, CancellationToken ct)
    {
        var result = await orchestrator.SaveServerAsync(id, request, ct);
        return result.Success ? Ok(result) : BadRequest(new MessageResponse(result.Message ?? "保存服务配置失败"));
    }

    [HttpDelete("Servers/{id}")]
    public async Task<IResult> DeleteServer(string id, [FromQuery] ulong expectedRevision, CancellationToken ct)
    {
        var result = await orchestrator.DeleteServerAsync(id, expectedRevision, ct);
        return result.Success ? Ok(result) : BadRequest(new MessageResponse(result.Message ?? "删除服务失败"));
    }

    [HttpPost("Servers/{id}/Probe")]
    public async Task<IResult> Probe(string id, CancellationToken ct)
    {
        var result = await orchestrator.ProbeAsync(id, ct);
        return Ok(result);
    }

    [HttpPost("Servers/{id}/Discover")]
    public async Task<IResult> Discover(string id, CancellationToken ct)
    {
        var result = await orchestrator.DiscoverAsync(id, ct);
        return Ok(result);
    }

    [HttpGet("Servers/{id}/Tools")]
    public async Task<IResult> GetTools(string id, [FromQuery] string? cursor, CancellationToken ct)
    {
        var result = await orchestrator.GetToolsAsync(id, cursor, ct);
        return Ok(result);
    }

    [HttpPut("Servers/{id}/Selection")]
    public async Task<IResult> SaveSelection(string id, [FromBody] SaveSelectionRequest request, CancellationToken ct)
    {
        var result = await orchestrator.SaveSelectionAsync(id, request, ct);
        return result.Success ? Ok(result) : BadRequest(new MessageResponse(result.Message ?? "保存工具勾选失败"));
    }

    [HttpPost("Apply")]
    public async Task<IResult> Apply([FromBody] ApplyConfigRequest request, CancellationToken ct)
    {
        var result = await orchestrator.ApplyAsync(request, ct);
        return result.Success ? Ok(result) : BadRequest(new MessageResponse(result.Message ?? "应用变更失败"));
    }

    [HttpGet("Audit")]
    public async Task<IResult> GetAudit([FromQuery] string? cursor, [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        var result = await orchestrator.GetAuditAsync(cursor, limit, ct);
        return Ok(result);
    }
}
