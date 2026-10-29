using LinuxWebTool.WebHost.Composition;
namespace LinuxWebTool.WebHost.Features.Security.Routes;

/// <summary>API Key 密钥管理控制器</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ApiKeysController(
    ApiKeyStore store,
    ApiKeyService service,
    IOperationLogger operationLogger) : MinimalApi.ControllerBase
{
    [HttpGet]
    public async Task<IResult> GetAll()
    {
        var list = await store.GetAllAsync();
        var dtos = list.Select(k => new ApiKeyItemResponse(
            k.Id,
            k.Name,
            k.KeyPrefix,
            k.IsEnabled,
            k.AllowApi,
            k.AllowMcp,
            k.AllowTerminal,
            k.AllowSchedules,
            k.AllowFiles,
            k.AllowTranscode,
            k.AllowGateway,
            k.AllowRemoteHosts,
            k.AllowedPaths,
            k.AllowedHostIds,
            k.CreatedAt,
            k.LastUsedAt,
            k.ExpiresAt,
            ApiKeyService.ParseToolsList(k.AllowedTools)
        )).ToList();
        return Ok(dtos);
    }

    [HttpPost]
    public async Task<IResult> Create([FromBody] CreateApiKeyRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new MessageResponse("密钥名称不能为空"));
        }

        var (rawKey, entity) = await service.CreateAsync(request);
        await operationLogger.LogAsync("新建 API Key", "安全凭据", entity.Name, entity.KeyPrefix, clientIp: HttpContext.GetClientIp());

        var response = new ApiKeyCreatedResponse(
            entity.Id,
            entity.Name,
            rawKey,
            entity.KeyPrefix,
            entity.AllowApi,
            entity.AllowMcp,
            entity.AllowTerminal,
            entity.AllowSchedules,
            entity.AllowFiles,
            entity.AllowTranscode,
            entity.AllowGateway,
            entity.AllowRemoteHosts,
            entity.AllowedPaths,
            entity.AllowedHostIds,
            entity.CreatedAt,
            entity.ExpiresAt,
            ApiKeyService.ParseToolsList(entity.AllowedTools));

        return Ok(response);
    }

    [HttpPut("{id}")]
    public async Task<IResult> Update(string id, [FromBody] UpdateApiKeyRequest request)
    {
        var entity = await store.GetByIdAsync(id);
        if (entity is null)
        {
            return NotFound(new MessageResponse("API Key 不存在"));
        }

        entity.Name = string.IsNullOrWhiteSpace(request.Name) ? entity.Name : request.Name.Trim();
        entity.IsEnabled = request.IsEnabled;
        entity.AllowApi = request.AllowApi;
        entity.AllowMcp = request.AllowMcp;
        entity.AllowTerminal = request.AllowTerminal;
        entity.AllowSchedules = request.AllowSchedules;
        entity.AllowFiles = request.AllowFiles;
        entity.AllowTranscode = request.AllowTranscode;
        entity.AllowGateway = request.AllowGateway;
        entity.AllowRemoteHosts = request.AllowRemoteHosts;
        entity.AllowedPaths = string.IsNullOrWhiteSpace(request.AllowedPaths) ? null : request.AllowedPaths.Trim();
        entity.AllowedHostIds = string.IsNullOrWhiteSpace(request.AllowedHostIds) ? null : request.AllowedHostIds.Trim();
        entity.AllowedTools = request.AllowedTools != null && request.AllowedTools.Count > 0 ? string.Join(",", request.AllowedTools) : null;
        entity.ExpiresAt = request.ExpiresAt;

        await store.UpdateAsync(entity);
        service.InvalidateCache(entity.KeyHash);
        await operationLogger.LogAsync("修改 API Key", "安全凭据", entity.Name, $"启用={entity.IsEnabled}", clientIp: HttpContext.GetClientIp());

        return Ok(new MessageResponse("更新成功"));
    }

    [HttpDelete("{id}")]
    public async Task<IResult> Delete(string id)
    {
        var entity = await store.GetByIdAsync(id);
        if (entity is null)
        {
            return NotFound(new MessageResponse("API Key 不存在"));
        }

        await store.DeleteAsync(id);
        service.InvalidateCache(entity.KeyHash);
        await operationLogger.LogAsync("删除 API Key", "安全凭据", entity.Name, entity.KeyPrefix, clientIp: HttpContext.GetClientIp());

        return Ok(new MessageResponse("删除成功"));
    }

    [HttpPost("{id}/Toggle")]
    public async Task<IResult> Toggle(string id)
    {
        var entity = await store.GetByIdAsync(id);
        if (entity is null)
        {
            return NotFound(new MessageResponse("API Key 不存在"));
        }

        entity.IsEnabled = !entity.IsEnabled;
        await store.UpdateAsync(entity);
        service.InvalidateCache(entity.KeyHash);
        await operationLogger.LogAsync("切换 API Key 状态", "安全凭据", entity.Name, $"状态={entity.IsEnabled}", clientIp: HttpContext.GetClientIp());

        return Ok(new MessageResponse(entity.IsEnabled ? "已启用" : "已禁用"));
    }

    [HttpPost("{id}/Clone")]
    public async Task<IResult> Clone(string id, [FromBody] CloneApiKeyRequest? request = null)
    {
        var source = await store.GetByIdAsync(id);
        if (source is null)
        {
            return NotFound(new MessageResponse("原 API Key 不存在"));
        }

        var newName = !string.IsNullOrWhiteSpace(request?.Name)
            ? request.Name.Trim()
            : $"{source.Name} - 副本";

        var createReq = new CreateApiKeyRequest(
            newName,
            source.AllowApi,
            source.AllowMcp,
            source.AllowTerminal,
            source.AllowSchedules,
            source.AllowFiles,
            source.AllowTranscode,
            source.AllowGateway,
            source.AllowRemoteHosts,
            source.AllowedPaths,
            source.AllowedHostIds,
            request?.ExpiresAt ?? source.ExpiresAt,
            ApiKeyService.ParseToolsList(source.AllowedTools));

        var (rawKey, entity) = await service.CreateAsync(createReq);
        await operationLogger.LogAsync("克隆 API Key", "安全凭据", entity.Name, $"克隆自={source.Name}, 前缀={entity.KeyPrefix}", clientIp: HttpContext.GetClientIp());

        var response = new ApiKeyCreatedResponse(
            entity.Id,
            entity.Name,
            rawKey,
            entity.KeyPrefix,
            entity.AllowApi,
            entity.AllowMcp,
            entity.AllowTerminal,
            entity.AllowSchedules,
            entity.AllowFiles,
            entity.AllowTranscode,
            entity.AllowGateway,
            entity.AllowRemoteHosts,
            entity.AllowedPaths,
            entity.AllowedHostIds,
            entity.CreatedAt,
            entity.ExpiresAt,
            ApiKeyService.ParseToolsList(entity.AllowedTools));

        return Ok(response);
    }

    [HttpGet("McpCatalog")]
    public IResult GetMcpCatalog([FromServices] Mcp.Engine.McpServerEngine mcpEngine)
    {
        return Ok(mcpEngine.GetCatalog());
    }
}
