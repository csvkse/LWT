using LinuxWebTool.Contracts.Features.Services.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LinuxWebTool.WebHost.Features.Services.Routes;

/// <summary>
/// 系统与基础设施运维中枢 (Services Hub) REST 控制器。
/// 专职负责 Linux 宿主系统级设施：Systemd、Docker 容器引擎、SSH、Samba、网络与防火墙。
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ServicesController(
    IServicesHubOrchestrator orchestrator,
    ISystemdServiceAdapter systemdAdapter,
    IDockerEngineAdapter dockerAdapter,
    ISystemServiceConfigAdapter configAdapter) : MinimalApi.ControllerBase
{
    [HttpGet("Environment")]
    public async Task<IResult> GetEnvironment(CancellationToken cancellationToken)
    {
        var env = await orchestrator.GetEnvironmentAsync(cancellationToken);
        return Ok(env);
    }

    #region Systemd
    [HttpGet("Systemd/Units")]
    public async Task<IResult> GetSystemdUnits(
        [FromQuery] string? keyword,
        [FromQuery] string? state,
        [FromQuery] string? type,
        [FromQuery] string? category,
        [FromQuery] string? scope,
        CancellationToken cancellationToken)
    {
        var criteria = new SystemdQueryCriteria
        {
            FilterKeyword = keyword,
            ActiveState = state,
            UnitType = type,
            Category = category,
            Scope = scope
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

    [HttpGet("Docker/DiskUsage")]
    public async Task<IResult> GetDockerDiskUsage(CancellationToken cancellationToken)
    {
        var usage = await dockerAdapter.GetDiskUsageAsync(cancellationToken);
        return usage != null ? Ok(usage) : BadRequest(new ServiceActionResult { Success = false, Message = "获取 Docker 磁盘占用失败" });
    }

    [HttpPost("Docker/Prune")]
    public async Task<IResult> PruneDocker([FromBody] DockerPruneRequest request, CancellationToken cancellationToken)
    {
        var result = await dockerAdapter.PruneAsync(request, cancellationToken);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost("Docker/Containers/{containerId}/Exec")]
    public async Task<IResult> ExecuteContainerCommand(string containerId, [FromBody] DockerContainerExecRequest request, CancellationToken cancellationToken)
    {
        var result = await dockerAdapter.ExecuteCommandAsync(containerId, request, cancellationToken);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpGet("Docker/Containers/{containerId}/Inspect")]
    public async Task<IResult> InspectContainer(string containerId, CancellationToken cancellationToken)
    {
        var inspect = await dockerAdapter.InspectContainerAsync(containerId, cancellationToken);
        return inspect != null ? Ok(inspect) : NotFound(new MessageResponse($"容器 [{containerId}] 不存在"));
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
    public async Task<IResult> UpdateSambaConfig([FromBody] SaveSambaConfigRequest request, CancellationToken cancellationToken)
    {
        var res = await configAdapter.SaveSambaConfigAsync(request, cancellationToken);
        return res.Success ? Ok(res) : BadRequest(res);
    }

    [HttpGet("Config/Samba/Shares/{shareName}")]
    public async Task<IResult> GetSambaShare(string shareName, CancellationToken cancellationToken)
    {
        var share = await configAdapter.GetSambaShareAsync(shareName, cancellationToken);
        return share != null ? Ok(share) : NotFound(new MessageResponse($"未找到共享 [{shareName}]"));
    }

    [HttpPost("Config/Samba/Shares")]
    public async Task<IResult> CreateSambaShare([FromBody] SaveSingleSambaShareRequest request, CancellationToken cancellationToken)
    {
        var res = await configAdapter.CreateSambaShareAsync(request, cancellationToken);
        return res.Success ? Ok(res) : BadRequest(res);
    }

    [HttpPut("Config/Samba/Shares/{shareName}")]
    public async Task<IResult> UpdateSambaShare(string shareName, [FromBody] SaveSingleSambaShareRequest request, CancellationToken cancellationToken)
    {
        var res = await configAdapter.UpdateSambaShareAsync(shareName, request, cancellationToken);
        return res.Success ? Ok(res) : BadRequest(res);
    }

    [HttpDelete("Config/Samba/Shares/{shareName}")]
    public async Task<IResult> DeleteSambaShare(string shareName, CancellationToken cancellationToken)
    {
        var res = await configAdapter.DeleteSambaShareAsync(shareName, cancellationToken);
        return res.Success ? Ok(res) : BadRequest(res);
    }

    [HttpGet("Config/Samba/Sessions")]
    public async Task<IResult> GetSambaSessions(CancellationToken cancellationToken)
    {
        var sessions = await configAdapter.GetSambaSessionsAsync(cancellationToken);
        return Ok(sessions);
    }

    [HttpGet("Config/Samba/Users")]
    public async Task<IResult> ListSambaUsers(CancellationToken cancellationToken)
    {
        var users = await configAdapter.ListSambaUsersAsync(cancellationToken);
        return Ok(users);
    }

    [HttpPost("Config/Samba/Users")]
    public async Task<IResult> CreateSambaUser([FromBody] CreateSambaUserRequest request, CancellationToken cancellationToken)
    {
        var res = await configAdapter.CreateSambaUserAsync(request, cancellationToken);
        return res.Success ? Ok(res) : BadRequest(res);
    }

    [HttpPut("Config/Samba/Users/Password")]
    public async Task<IResult> UpdateSambaUserPassword([FromBody] UpdateSambaUserPasswordRequest request, CancellationToken cancellationToken)
    {
        var res = await configAdapter.UpdateSambaUserPasswordAsync(request, cancellationToken);
        return res.Success ? Ok(res) : BadRequest(res);
    }

    [HttpDelete("Config/Samba/Users/{username}")]
    public async Task<IResult> DeleteSambaUser(string username, CancellationToken cancellationToken)
    {
        var res = await configAdapter.DeleteSambaUserAsync(username, cancellationToken);
        return res.Success ? Ok(res) : BadRequest(res);
    }

    [HttpPost("Config/Samba/Install")]
    public async Task<IResult> InstallSambaOnHost(CancellationToken cancellationToken)
    {
        var res = await configAdapter.InstallSambaOnHostAsync(cancellationToken);
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
}
