using LinuxWebTool.WebHost.Composition;
namespace LinuxWebTool.WebHost.Features.Tunnel.Routes;

/// <summary>
/// ProxyByCF FRP 反向穿透控制器（支持多线路、302 内网智能代理与 HTTP/SOCKS 上游代理）
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class FrpTunnelController(
    FrpTunnelConfigStore configStore,
    FrpTunnelManager manager,
    IOperationLogger operationLogger) : MinimalApi.ControllerBase
{
    // === 多线路管理 API ===

    [HttpGet("Lines")]
    public async Task<IResult> GetLines()
    {
        var lines = await manager.GetAllLinesWithStatusAsync();
        // 敏感 Key 脱敏
        var masked = lines.Select(MaskLine).ToList();
        return Ok(masked);
    }

    [HttpGet("Lines/{id}")]
    public async Task<IResult> GetLine(string id)
    {
        var line = await manager.GetLineWithStatusAsync(id);
        if (line == null) return NotFound(new MessageResponse("线路不存在"));
        return Ok(MaskLine(line));
    }

    [HttpPost("Lines")]
    public async Task<IResult> CreateLine([FromBody] CreateFrpTunnelLineRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return BadRequest(new MessageResponse("线路名称不能为空"));
        if (string.IsNullOrWhiteSpace(request.ServerUrl)) return BadRequest(new MessageResponse("服务端地址不能为空"));
        if (string.IsNullOrWhiteSpace(request.TunnelHost)) return BadRequest(new MessageResponse("穿透 Host 不能为空"));

        var created = await manager.CreateLineAsync(request);
        await operationLogger.LogAsync("新建 FRP 穿透线路", "公网穿透", created.Name, created.TunnelHost, clientIp: HttpContext.GetClientIp());
        return Ok(MaskLine(created));
    }

    [HttpPut("Lines/{id}")]
    public async Task<IResult> UpdateLine(string id, [FromBody] UpdateFrpTunnelLineRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return BadRequest(new MessageResponse("线路名称不能为空"));
        if (string.IsNullOrWhiteSpace(request.ServerUrl)) return BadRequest(new MessageResponse("服务端地址不能为空"));
        if (string.IsNullOrWhiteSpace(request.TunnelHost)) return BadRequest(new MessageResponse("穿透 Host 不能为空"));

        var updated = await manager.UpdateLineAsync(id, request);
        if (updated == null) return NotFound(new MessageResponse("线路不存在"));

        await operationLogger.LogAsync("更新 FRP 穿透线路", "公网穿透", updated.Name, updated.TunnelHost, clientIp: HttpContext.GetClientIp());
        return Ok(MaskLine(updated));
    }

    [HttpDelete("Lines/{id}")]
    public async Task<IResult> DeleteLine(string id)
    {
        var line = await manager.GetLineWithStatusAsync(id);
        if (line == null) return NotFound(new MessageResponse("线路不存在"));

        await manager.DeleteLineAsync(id);
        await operationLogger.LogAsync("删除 FRP 穿透线路", "公网穿透", line.Name, line.TunnelHost, clientIp: HttpContext.GetClientIp());
        return Ok(new MessageResponse("线路已删除"));
    }

    [HttpPost("Lines/{id}/Start")]
    public async Task<IResult> StartLine(string id)
    {
        await manager.StartLineAsync(id);
        await operationLogger.LogAsync("启动 FRP 穿透线路", "公网穿透", id, "", clientIp: HttpContext.GetClientIp());
        return Ok(new MessageResponse("已触发启动连接"));
    }

    [HttpPost("Lines/{id}/Stop")]
    public async Task<IResult> StopLine(string id)
    {
        await manager.StopLineAsync(id);
        await operationLogger.LogAsync("停止 FRP 穿透线路", "公网穿透", id, "", clientIp: HttpContext.GetClientIp());
        return Ok(new MessageResponse("已断开穿透连接"));
    }

    [HttpGet("Lines/{id}/Logs")]
    public IResult GetLineLogs(string id)
    {
        var logs = manager.GetLineLogs(id);
        return Ok(logs.ToList());
    }

    // === 兼容单线路 API (默认线路操作) ===

    [HttpGet("Config")]
    public async Task<IResult> GetConfig()
    {
        var lines = await manager.GetAllLinesWithStatusAsync();
        if (lines.Count > 0)
        {
            var first = lines[0];
            return Ok(new FrpTunnelConfigDto(
                first.ServerUrl,
                first.TunnelHost,
                MaskKey(first.ApiKey),
                first.LocalTargetUrl,
                first.AutoStart,
                first.HeartbeatIntervalSeconds,
                first.UpdateTime));
        }

        var config = await configStore.GetConfigAsync();
        return Ok(new FrpTunnelConfigDto(
            config.ServerUrl,
            config.TunnelHost,
            MaskKey(config.ApiKey),
            config.LocalTargetUrl,
            config.AutoStart,
            config.HeartbeatIntervalSeconds,
            config.UpdateTime));
    }

    [HttpPut("Config")]
    public async Task<IResult> UpdateConfig([FromBody] UpdateFrpConfigRequest request)
    {
        var lines = await manager.GetAllLinesWithStatusAsync();
        if (lines.Count > 0)
        {
            var first = lines[0];
            await manager.UpdateLineAsync(first.Id, new UpdateFrpTunnelLineRequest(
                first.Name,
                request.ServerUrl.Trim(),
                first.BackupServerUrls,
                request.TunnelHost.Trim().ToLowerInvariant(),
                request.ApiKey.Contains('*') ? first.ApiKey : request.ApiKey.Trim(),
                string.IsNullOrWhiteSpace(request.LocalTargetUrl) ? "http://127.0.0.1:8080" : request.LocalTargetUrl.Trim(),
                request.AutoStart,
                request.HeartbeatIntervalSeconds > 0 ? request.HeartbeatIntervalSeconds : 15,
                first.EnableLan302Proxy,
                first.ProxyType,
                first.ProxyUrl,
                first.ProxyBypass,
                first.SortOrder));
        }

        var legacyConfig = await configStore.GetConfigAsync();
        legacyConfig.ServerUrl = request.ServerUrl.Trim();
        legacyConfig.TunnelHost = request.TunnelHost.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(request.ApiKey) && !request.ApiKey.Contains('*'))
        {
            legacyConfig.ApiKey = request.ApiKey.Trim();
        }
        legacyConfig.LocalTargetUrl = string.IsNullOrWhiteSpace(request.LocalTargetUrl) ? "http://127.0.0.1:8080" : request.LocalTargetUrl.Trim();
        legacyConfig.AutoStart = request.AutoStart;
        legacyConfig.HeartbeatIntervalSeconds = request.HeartbeatIntervalSeconds > 0 ? request.HeartbeatIntervalSeconds : 15;
        await configStore.SaveConfigAsync(legacyConfig);

        await operationLogger.LogAsync("更新 FRP 配置", "公网穿透", request.TunnelHost, request.ServerUrl, clientIp: HttpContext.GetClientIp());
        return Ok(new MessageResponse("配置已保存"));
    }

    [HttpGet("Status")]
    public async Task<IResult> GetStatus()
    {
        var lines = await manager.GetAllLinesWithStatusAsync();
        if (lines.Count > 0)
        {
            var first = lines[0];
            return Ok(new FrpTunnelStatusDto(
                first.State,
                first.PublicUrl,
                first.SubdomainUrl,
                first.LocalTargetUrl,
                first.State == "Connected" ? DateTime.UtcNow.AddSeconds(-first.UptimeSeconds) : null,
                first.UptimeSeconds,
                first.SentBytes,
                first.ReceivedBytes,
                first.LastError));
        }

        return Ok(new FrpTunnelStatusDto("Disconnected", null, null, null, null, 0, 0, 0, null));
    }

    [HttpPost("Start")]
    public async Task<IResult> Start()
    {
        var lines = await manager.GetAllLinesWithStatusAsync();
        if (lines.Count > 0)
        {
            await manager.StartLineAsync(lines[0].Id);
        }
        await operationLogger.LogAsync("启动 FRP 穿透", "公网穿透", "默认线路", "", clientIp: HttpContext.GetClientIp());
        return Ok(new MessageResponse("已提交启动请求"));
    }

    [HttpPost("Stop")]
    public async Task<IResult> Stop()
    {
        var lines = await manager.GetAllLinesWithStatusAsync();
        if (lines.Count > 0)
        {
            await manager.StopLineAsync(lines[0].Id);
        }
        await operationLogger.LogAsync("停止 FRP 穿透", "公网穿透", "默认线路", "", clientIp: HttpContext.GetClientIp());
        return Ok(new MessageResponse("已停止穿透长连接"));
    }

    [HttpGet("Logs")]
    public IResult GetLogs()
    {
        var logs = manager.GetAllLogs();
        return Ok(logs.ToList());
    }

    private static string MaskKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;
        return key.Length > 8 ? $"{key[..4]}****{key[^4..]}" : "********";
    }

    private static FrpTunnelLineDto MaskLine(FrpTunnelLineDto line) => line with
    {
        ApiKey = MaskKey(line.ApiKey)
    };
}
