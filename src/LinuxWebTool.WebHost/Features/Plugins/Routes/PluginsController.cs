using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Contracts.Shared.Support;
using LinuxWebTool.Infrastructure.Features.Plugins;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LinuxWebTool.WebHost.Features.Plugins.Routes;

/// <summary>
/// 统一插件中心平台 (Plugin Platform) REST 控制器。
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PluginsController(IPluginManager pluginManager) : MinimalApi.ControllerBase
{
    [HttpGet("SystemMode")]
    [AllowAnonymous]
    public IResult GetSystemMode()
    {
        var status = PluginSafeModeGuard.GetSystemMode();
        if (status.IsSafeMode)
        {
            Response.Headers["X-LWT-Safe-Mode"] = "true";
        }
        return Ok(status);
    }

    [HttpGet]
    public async Task<IResult> GetAll(
        [FromQuery] string? profile,
        [FromQuery] string? search,
        CancellationToken ct)
    {
        if (HttpContext != null)
        {
            HttpContext.Response.Headers["X-LWT-Safe-Mode"] = PluginSafeModeGuard.IsSafeModeEnabled() ? "true" : "false";
        }
        PluginProfile? profileFilter = null;
        if (!string.IsNullOrWhiteSpace(profile))
        {
            if (profile.Equals("resident", StringComparison.OrdinalIgnoreCase)) profileFilter = PluginProfile.Resident;
            else if (profile.Equals("oneshot", StringComparison.OrdinalIgnoreCase)) profileFilter = PluginProfile.OneShot;
        }

        var list = await pluginManager.GetAllAsync(profileFilter, ct);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            list = list.Where(p =>
                p.Manifest.Name.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                p.Manifest.Id.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                p.Manifest.Description.Contains(s, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return Ok(list);
    }

    [HttpGet("{id}")]
    public async Task<IResult> GetById(string id, CancellationToken ct)
    {
        if (!PluginSecurityGuard.IsValidPluginId(id)) return NotFound(new MessageResponse("非法插件 ID"));
        var item = await pluginManager.GetByIdAsync(id, ct);
        return item != null ? Ok(item) : NotFound(new MessageResponse($"未找到插件: {id}"));
    }

    [HttpPost("{id}/Start")]
    public async Task<IResult> Start(string id, CancellationToken ct)
    {
        var plugin = await pluginManager.GetByIdAsync(id, ct);
        if (plugin?.Profile == PluginProfile.OneShot)
        {
            var defaultAction = plugin.Manifest.CustomActions?.FirstOrDefault()?.Action ?? "";
            var actResult = await pluginManager.ExecuteActionAsync(new PluginActionRequest
            {
                PluginId = id,
                Action = defaultAction
            }, ct);

            return actResult.Success
                ? Ok(new MessageResponse(actResult.Message ?? "轻量脚本任务已执行完成"))
                : BadRequest(new MessageResponse(actResult.Message ?? "轻量脚本执行失败"));
        }

        var (success, errorMsg) = await pluginManager.StartWithResultAsync(id, ct);
        return success
            ? Ok(new MessageResponse("插件已启动"))
            : BadRequest(new MessageResponse(errorMsg ?? "启动失败"));
    }

    [HttpPost("{id}/Stop")]
    public async Task<IResult> Stop(string id, [FromQuery] bool? manual = null, CancellationToken ct = default)
    {
        var isManual = manual ?? true;
        var success = await pluginManager.StopAsync(id, isManual, ct);
        return Ok(new MessageResponse(success ? "插件已停止" : "停止失败"));
    }

    [HttpPost("{id}/Restart")]
    public async Task<IResult> Restart(string id, CancellationToken ct)
    {
        var success = await pluginManager.RestartAsync(id, ct);
        return success ? Ok(new MessageResponse("插件已重启")) : BadRequest(new MessageResponse("重启失败"));
    }

    [HttpGet("{id}/Port/Check")]
    public async Task<IResult> CheckPort(string id, [FromQuery] int port, CancellationToken ct)
    {
        var result = await pluginManager.CheckPortAsync(id, port, ct);
        return Ok(result);
    }

    [HttpPost("{id}/Port/Switch")]
    public async Task<IResult> SwitchPort(string id, [FromBody] PluginPortSwitchRequest request, CancellationToken ct)
    {
        var result = await pluginManager.SwitchPortAsync(id, request.NewPort, request.RestartIfRunning, ct);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpDelete("{id}")]
    public async Task<IResult> Delete(string id, CancellationToken ct)
    {
        var success = await pluginManager.DeleteAsync(id, ct);
        return Ok(new MessageResponse(success ? "插件已删除" : "删除失败"));
    }

    [HttpPost]
    public async Task<IResult> CreateOrUpdate([FromBody] PluginCreateOrUpdateRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Manifest.Id) || string.IsNullOrWhiteSpace(request.Manifest.Name))
        {
            return BadRequest(new MessageResponse("插件 ID 和名称不能为空"));
        }
        var item = await pluginManager.CreateOrUpdateAsync(request, ct);
        return Ok(item);
    }

    [HttpPost("{id}/SetEnabled")]
    public async Task<IResult> SetEnabled(string id, [FromQuery] bool? enabled, CancellationToken ct)
    {
        var plugin = await pluginManager.GetByIdAsync(id, ct);
        var targetEnabled = enabled ?? !(plugin?.Manifest.IsEnabled ?? true);
        var success = await pluginManager.SetEnabledAsync(id, targetEnabled, ct);
        return Ok(new MessageResponse(success ? (targetEnabled ? "插件已启用" : "插件已禁用") : "更新状态失败"));
    }

    [HttpGet("{id}/Logs")]
    public async Task<IResult> GetLogs(string id, [FromQuery] int limit = 200, CancellationToken ct = default)
    {
        var lines = await pluginManager.GetLogsAsync(id, limit, ct);
        return Ok(lines);
    }

    [HttpPost("Action")]
    public async Task<IResult> ExecuteAction([FromBody] PluginActionRequest request, CancellationToken ct)
    {
        var result = await pluginManager.ExecuteActionAsync(request, ct);
        return Ok(result);
    }

    [HttpGet("{id}/Assets")]
    [HttpGet("{id}/Assets/{**filePath}")]
    [AllowAnonymous]
    public async Task<IResult> GetAsset(string id, string? filePath = null, CancellationToken ct = default)
    {
        if (!PluginSecurityGuard.IsValidPluginId(id))
        {
            return NotFound(new MessageResponse("非法插件 ID"));
        }

        var assetPath = pluginManager.ResolvePluginAssetPath(id, filePath);
        if (string.IsNullOrEmpty(assetPath) || !System.IO.File.Exists(assetPath))
        {
            return NotFound(new MessageResponse("未找到请求的静态插件资产文件"));
        }

        var ext = Path.GetExtension(assetPath)?.ToLowerInvariant() ?? string.Empty;
        var contentType = ext switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".js" or ".mjs" => "application/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".ico" => "image/x-icon",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            ".txt" or ".log" => "text/plain; charset=utf-8",
            _ => "application/octet-stream"
        };

        var bytes = await System.IO.File.ReadAllBytesAsync(assetPath, ct);
        return Results.File(bytes, contentType, enableRangeProcessing: true);
    }

    [HttpGet("{id}/Code")]
    public async Task<IResult> GetCode(string id, [FromQuery] string? file, CancellationToken ct)
    {
        try
        {
            var code = await pluginManager.GetCodeAsync(id, file, ct);
            return Ok(code);
        }
        catch (DirectoryNotFoundException)
        {
            return NotFound(new MessageResponse($"未找到插件: {id}"));
        }
        catch (Exception ex)
        {
            return BadRequest(new MessageResponse(ex.Message));
        }
    }

    [HttpPost("{id}/Code")]
    public async Task<IResult> SaveCode(string id, [FromBody] PluginCodeSaveRequest request, CancellationToken ct)
    {
        try
        {
            var success = await pluginManager.SaveCodeAsync(id, request, ct);
            return Ok(new MessageResponse(success ? "代码已保存" : "保存失败"));
        }
        catch (Exception ex)
        {
            return BadRequest(new MessageResponse(ex.Message));
        }
    }

    [HttpPost("{id}/Files/Upload")]
    [Consumes("multipart/form-data")]
    public async Task<IResult> UploadFile(string id, [FromQuery] string? path, IFormFile file, CancellationToken ct)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new MessageResponse("请选择要上传的文件"));
        }

        var relativePath = string.IsNullOrWhiteSpace(path) ? file.FileName : path;
        try
        {
            await using var stream = file.OpenReadStream();
            var success = await pluginManager.UploadFileAsync(id, relativePath, stream, ct);
            return success ? Ok(new MessageResponse("文件上传成功")) : BadRequest(new MessageResponse("文件上传失败"));
        }
        catch (Exception ex)
        {
            return BadRequest(new MessageResponse(ex.Message));
        }
    }

    [HttpGet("{id}/Files/Download")]
    public async Task<IResult> DownloadFile(string id, [FromQuery] string file, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return BadRequest(new MessageResponse("文件名不能为空"));
        }

        try
        {
            var (stream, fileName, contentType) = await pluginManager.DownloadFileAsync(id, file, ct);
            return Results.File(stream, contentType, fileDownloadName: fileName);
        }
        catch (FileNotFoundException)
        {
            return NotFound(new MessageResponse($"未找到文件: {file}"));
        }
        catch (Exception ex)
        {
            return BadRequest(new MessageResponse(ex.Message));
        }
    }

    [HttpDelete("{id}/Files")]
    public async Task<IResult> DeleteFile(string id, [FromQuery] string file, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return BadRequest(new MessageResponse("文件名不能为空"));
        }

        try
        {
            var success = await pluginManager.DeleteFileAsync(id, file, ct);
            return success ? Ok(new MessageResponse("文件已删除")) : BadRequest(new MessageResponse("文件删除失败"));
        }
        catch (Exception ex)
        {
            return BadRequest(new MessageResponse(ex.Message));
        }
    }

    [HttpPost("{id}/Debug")]
    public async Task<IResult> DebugRun(string id, [FromBody] PluginDebugRunRequest request, CancellationToken ct)
    {
        var result = await pluginManager.DebugRunAsync(id, request, ct);
        return Ok(result);
    }

    [HttpPost("Import/Zip")]
    [Consumes("multipart/form-data")]
    public async Task<IResult> ImportZip(IFormFile file, CancellationToken ct)
    {
        if (file == null || file.Length == 0) return BadRequest(new MessageResponse("请选择有效的 Zip 文件上传。"));
        using var stream = file.OpenReadStream();
        try
        {
            var result = await pluginManager.ImportZipAsync(stream, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new MessageResponse(ex.Message));
        }
    }

    [HttpPost("Repo/Scan")]
    public async Task<IResult> ScanRepo([FromBody] PluginRepoScanRequest request, CancellationToken ct)
    {
        try
        {
            var result = await pluginManager.ScanRepoAsync(request.RepoUrl, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new MessageResponse(ex.Message));
        }
    }

    [HttpPost("Repo/Apply")]
    public async Task<IResult> ApplyRepoModules([FromBody] PluginRepoApplyRequest request, CancellationToken ct)
    {
        try
        {
            var result = await pluginManager.ApplyRepoModulesAsync(request, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new MessageResponse(ex.Message));
        }
    }

    [HttpGet("{id}/Source")]
    public async Task<IResult> GetSource(string id, CancellationToken ct)
    {
        var meta = await pluginManager.GetSourceMetadataAsync(id, ct);
        return meta != null ? Ok(meta) : NotFound(new MessageResponse("未找到来源元数据"));
    }

    [HttpGet("{id}/Storage/Sqlite")]
    public async Task<IResult> GetSqliteInfo(string id, CancellationToken ct)
    {
        var info = await pluginManager.GetSqliteInfoAsync(id, ct);
        return Ok(info);
    }

    private bool IsStorageAuthorized(string id)
    {
        if (User.Identity?.IsAuthenticated == true) return true;
        var incomingSecret = Request.Headers["X-Lwt-Storage-Secret"].FirstOrDefault();
        var validSecret = pluginManager.GetInternalSecret(id);
        return !string.IsNullOrEmpty(incomingSecret) && !string.IsNullOrEmpty(validSecret) && string.Equals(incomingSecret, validSecret, StringComparison.Ordinal);
    }

    [HttpGet("{id}/Storage/Kv")]
    [AllowAnonymous]
    public async Task<IResult> GetKvList(string id, CancellationToken ct)
    {
        if (!IsStorageAuthorized(id)) return Unauthorized(new MessageResponse("未授权访问插件存储"));
        var list = await pluginManager.GetKvListAsync(id, ct);
        return Ok(list);
    }

    [HttpGet("{id}/Storage/Kv/{key}")]
    [AllowAnonymous]
    public async Task<IResult> GetKvItem(string id, string key, CancellationToken ct)
    {
        if (!IsStorageAuthorized(id)) return Unauthorized(new MessageResponse("未授权访问插件存储"));
        var item = await pluginManager.GetKvAsync(id, key, ct);
        return item != null ? Ok(item) : NotFound(new MessageResponse($"未找到 Key: {key}"));
    }

    [HttpPost("{id}/Storage/Kv")]
    [AllowAnonymous]
    public async Task<IResult> SetKv(string id, [FromBody] PluginKvItem item, CancellationToken ct)
    {
        if (!IsStorageAuthorized(id)) return Unauthorized(new MessageResponse("未授权访问插件存储"));
        var ok = await pluginManager.SetKvAsync(id, item, ct);
        return ok ? Ok(new MessageResponse("写入 KV 成功")) : BadRequest(new MessageResponse("写入 KV 失败"));
    }

    [HttpDelete("{id}/Storage/Kv/{key}")]
    [AllowAnonymous]
    public async Task<IResult> DeleteKv(string id, string key, CancellationToken ct)
    {
        if (!IsStorageAuthorized(id)) return Unauthorized(new MessageResponse("未授权访问插件存储"));
        var ok = await pluginManager.DeleteKvAsync(id, key, ct);
        return ok ? Ok(new MessageResponse("删除 KV 成功")) : BadRequest(new MessageResponse("删除 KV 失败"));
    }

    [HttpPost("{id}/Events/Publish")]
    [AllowAnonymous]
    public async Task<IResult> PublishEvent(
        string id,
        [FromBody] PluginEventPublishRequest request,
        [FromServices] LinuxWebTool.Infrastructure.Features.Plugins.Supervisor.PluginEventBus eventBus,
        CancellationToken ct)
    {
        if (!IsStorageAuthorized(id)) return Unauthorized(new MessageResponse("未授权访问插件事件总线"));
        if (string.IsNullOrWhiteSpace(request.EventType)) return BadRequest(new MessageResponse("EventType 不能为空"));

        await eventBus.PublishAsync(id, request.EventType, request.Payload, ct);
        return Ok(new MessageResponse("事件发布成功"));
    }

    [HttpGet("{id}/Events/Poll")]
    [AllowAnonymous]
    public async Task<IResult> PollEvents(
        string id,
        [FromQuery] string? eventType,
        [FromQuery] int limit,
        [FromServices] LinuxWebTool.Infrastructure.Features.Plugins.Supervisor.PluginEventBus eventBus,
        CancellationToken ct)
    {
        if (!IsStorageAuthorized(id)) return Unauthorized(new MessageResponse("未授权访问插件事件总线"));
        var events = await eventBus.PollEventsAsync(id, eventType, limit <= 0 ? 50 : limit, ct);
        return Ok(events);
    }

    [HttpGet("{id}/Revisions")]
    public async Task<IResult> ListRevisions(string id, CancellationToken ct)
    {
        var revisions = await pluginManager.ListRevisionsAsync(id, ct);
        return Ok(revisions);
    }

    [HttpPost("{id}/Revisions/Alias")]
    public async Task<IResult> SwitchRevisionAlias(
        string id,
        [FromBody] PluginRevisionAliasRequest request,
        CancellationToken ct)
    {
        var ok = await pluginManager.SwitchRevisionAliasAsync(id, request.Alias, request.RevisionName, ct);
        return ok ? Ok(new MessageResponse("别名切换成功")) : BadRequest(new MessageResponse("别名切换失败"));
    }

    [HttpGet("Discovery")]
    public async Task<IResult> DiscoverServices(CancellationToken ct)
    {
        var services = await pluginManager.DiscoverServicesAsync(ct);
        return Ok(services);
    }

    [HttpPost("Invoke")]
    public async Task<IResult> InvokeService(
        [FromBody] PluginInvokeRequest request,
        CancellationToken ct)
    {
        var response = await pluginManager.InvokeServiceAsync(request, ct);
        return Ok(response);
    }

    [HttpPost("{id}/Build")]
    public async Task<IResult> BuildPlugin(string id, CancellationToken ct)
    {
        var result = await pluginManager.BuildPluginAsync(id, ct);
        return Ok(result);
    }
}
