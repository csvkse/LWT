using LinuxWebTool.Contracts.Features.Services.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LinuxWebTool.WebHost.Features.Services.Routes;

/// <summary>
/// 系统与服务管理中枢 (Services Hub) REST 控制器。
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ServicesController(
    IServicesHubOrchestrator orchestrator,
    ISystemdServiceAdapter systemdAdapter,
    IDockerEngineAdapter dockerAdapter,
    ISystemServiceConfigAdapter configAdapter,
    IScriptExtensionManager extensionManager) : MinimalApi.ControllerBase
{
    [HttpGet("Environment")]
    public async Task<IResult> GetEnvironment(CancellationToken cancellationToken)
    {
        var env = await orchestrator.GetEnvironmentAsync(cancellationToken);
        return Ok(env);
    }

    #region Systemd
    [HttpGet("Systemd/Units")]
    public async Task<IResult> GetSystemdUnits([FromQuery] string? keyword, [FromQuery] string? state, [FromQuery] string? type, CancellationToken cancellationToken)
    {
        var criteria = new SystemdQueryCriteria
        {
            FilterKeyword = keyword,
            ActiveState = state,
            UnitType = type
        };
        var list = await systemdAdapter.ListUnitsAsync(criteria, cancellationToken);
        return Ok(list);
    }

    [HttpPost("Systemd/Action")]
    public async Task<IResult> ExecuteSystemdAction([FromBody] ServiceActionRequest request, CancellationToken cancellationToken)
    {
        var result = await systemdAdapter.ExecuteActionAsync(request.ServiceId, request.Action, cancellationToken);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpGet("Systemd/Journal")]
    public async Task<IResult> GetSystemdJournal([FromQuery] string unit, [FromQuery] int lines = 100, CancellationToken cancellationToken = default)
    {
        var result = await systemdAdapter.GetJournalAsync(new SystemdJournalRequest { UnitName = unit, Lines = lines }, cancellationToken);
        return Ok(result);
    }

    [HttpGet("Systemd/UnitFile")]
    public async Task<IResult> GetSystemdUnitFile([FromQuery] string unit, CancellationToken cancellationToken)
    {
        var content = await systemdAdapter.GetUnitFileContentAsync(unit, cancellationToken);
        return Ok(new SystemdUnitFileResponse { Unit = unit, Content = content });
    }
    #endregion

    #region Docker
    [HttpGet("Docker/Containers")]
    public async Task<IResult> GetDockerContainers([FromQuery] bool all = true, CancellationToken cancellationToken = default)
    {
        var list = await dockerAdapter.ListContainersAsync(all, cancellationToken);
        return Ok(list);
    }

    [HttpPost("Docker/Action")]
    public async Task<IResult> ExecuteDockerAction([FromBody] DockerContainerActionRequest request, CancellationToken cancellationToken)
    {
        var result = await dockerAdapter.ExecuteContainerActionAsync(request, cancellationToken);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpGet("Docker/Logs")]
    public async Task<IResult> GetDockerLogs([FromQuery] string containerId, [FromQuery] int tail = 200, CancellationToken cancellationToken = default)
    {
        var logs = await dockerAdapter.GetContainerLogsAsync(new DockerLogsRequest { ContainerId = containerId, Tail = tail }, cancellationToken);
        return Ok(new DockerContainerLogsResponse { ContainerId = containerId, Logs = logs });
    }

    [HttpGet("Docker/Images")]
    public async Task<IResult> GetDockerImages(CancellationToken cancellationToken)
    {
        var images = await dockerAdapter.ListImagesAsync(cancellationToken);
        return Ok(images);
    }
    #endregion

    #region Config
    [HttpGet("Config/Ssh")]
    public async Task<IResult> GetSshConfig(CancellationToken cancellationToken)
    {
        var cfg = await configAdapter.GetSshConfigAsync(cancellationToken);
        return Ok(cfg);
    }

    [HttpPost("Config/Ssh")]
    public async Task<IResult> UpdateSshConfig([FromBody] UpdateSshConfigRequest request, CancellationToken cancellationToken)
    {
        var res = await configAdapter.UpdateSshConfigAsync(request, cancellationToken);
        return res.Success ? Ok(res) : BadRequest(res);
    }

    [HttpGet("Config/Samba")]
    public async Task<IResult> GetSambaConfig(CancellationToken cancellationToken)
    {
        var cfg = await configAdapter.GetSambaConfigAsync(cancellationToken);
        return Ok(cfg);
    }

    [HttpPost("Config/Samba")]
    public async Task<IResult> UpdateSambaConfig([FromBody] IReadOnlyList<SambaShareItem> shares, CancellationToken cancellationToken)
    {
        var res = await configAdapter.UpdateSambaSharesAsync(shares, cancellationToken);
        return res.Success ? Ok(res) : BadRequest(res);
    }

    [HttpGet("Config/Network")]
    public async Task<IResult> GetNetworkInterfaces(CancellationToken cancellationToken)
    {
        var nics = await configAdapter.GetNetworkInterfacesAsync(cancellationToken);
        return Ok(nics);
    }

    [HttpGet("Config/Dns")]
    public async Task<IResult> GetDnsConfig(CancellationToken cancellationToken)
    {
        var dns = await configAdapter.GetDnsConfigAsync(cancellationToken);
        return Ok(dns);
    }

    [HttpGet("Config/Firewall")]
    public async Task<IResult> GetFirewallStatus(CancellationToken cancellationToken)
    {
        var fw = await configAdapter.GetFirewallStatusAsync(cancellationToken);
        return Ok(fw);
    }

    [HttpPost("Config/Firewall/Rule")]
    public async Task<IResult> UpdateFirewallRule([FromBody] UpdateFirewallRuleRequest request, CancellationToken cancellationToken)
    {
        var res = await configAdapter.UpdateFirewallRuleAsync(request, cancellationToken);
        return res.Success ? Ok(res) : BadRequest(res);
    }
    #endregion

    #region Extensions
    [HttpGet("Extensions")]
    public async Task<IResult> GetExtensions(CancellationToken cancellationToken)
    {
        var exts = await extensionManager.ListExtensionsAsync(cancellationToken);
        return Ok(exts);
    }

    [HttpPost("Extensions/Action")]
    public async Task<IResult> ExecuteExtensionAction([FromBody] ExtensionActionRequest request, CancellationToken cancellationToken)
    {
        var res = await extensionManager.ExecuteExtensionActionAsync(request, cancellationToken);
        return res.Success ? Ok(res) : BadRequest(res);
    }

    [HttpGet("Extensions/Logs")]
    public async Task<IResult> GetExtensionLogs([FromQuery] string id, [FromQuery] int lines = 100, CancellationToken cancellationToken = default)
    {
        var logs = await extensionManager.GetExtensionLogsAsync(id, lines, cancellationToken);
        return Ok(new ExtensionLogsResponse { ExtensionId = id, Logs = logs });
    }
    #endregion
}
