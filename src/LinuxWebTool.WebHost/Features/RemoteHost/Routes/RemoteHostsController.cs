using System.Diagnostics;
using LinuxWebTool.Contracts.Features.RemoteHost.Contracts;
using LinuxWebTool.Infrastructure.Features.Mount.Adapters;
using LinuxWebTool.Infrastructure.Features.RemoteHost.Adapters;
using LinuxWebTool.Infrastructure.Features.RemoteHost.Security;
using LinuxWebTool.Infrastructure.Shared.Persistence;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using LinuxWebTool.Infrastructure.Shared.Support;
using LinuxWebTool.WebHost.MinimalApi;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LinuxWebTool.WebHost.Features.RemoteHost.Routes;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class RemoteHostsController(
    RemoteHostStore store,
    HostCredentialProtector protector,
    RemoteHostClientFactory factory,
    RemoteTerminalSessionManager terminalSessions,
    SshHostKeyProbe hostKeyProbe,
    IOperationLogger operationLogger,
    ILogger<RemoteHostsController> logger) : MinimalApi.ControllerBase
{
    [HttpGet]
    public async Task<IResult> List()
    {
        var entities = await store.GetAllAsync();
        var dtos = entities.Select(e => new RemoteHostItemResponse(
            Id: e.Id,
            Name: e.Name,
            Host: e.Host,
            Port: e.Port,
            Protocol: e.Protocol,
            AuthType: e.AuthType,
            Username: e.Username,
            HasCredential: !string.IsNullOrEmpty(e.EncryptedCredential),
            HostKeyFingerprint: e.HostKeyFingerprint,
            Tags: e.Tags,
            Remark: e.Remark,
            SortOrder: e.SortOrder,
            CreatedAt: e.CreatedAt,
            UpdatedAt: e.UpdatedAt,
            LastConnectedAt: e.LastConnectedAt
        )).ToList();

        return Ok(dtos);
    }

    [HttpGet("{id}")]
    public async Task<IResult> Get(string id)
    {
        var entity = await store.GetByIdAsync(id);
        if (entity is null) return NotFound(new MessageResponse("远程主机不存在"));

        var dto = new RemoteHostItemResponse(
            Id: entity.Id,
            Name: entity.Name,
            Host: entity.Host,
            Port: entity.Port,
            Protocol: entity.Protocol,
            AuthType: entity.AuthType,
            Username: entity.Username,
            HasCredential: !string.IsNullOrEmpty(entity.EncryptedCredential),
            HostKeyFingerprint: entity.HostKeyFingerprint,
            Tags: entity.Tags,
            Remark: entity.Remark,
            SortOrder: entity.SortOrder,
            CreatedAt: entity.CreatedAt,
            UpdatedAt: entity.UpdatedAt,
            LastConnectedAt: entity.LastConnectedAt
        );

        return Ok(dto);
    }

    [HttpPost]
    public async Task<IResult> Create([FromBody] CreateRemoteHostRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Host))
        {
            return BadRequest(new MessageResponse("主机名称与地址不能为空"));
        }

        var (cipher, nonce, tag) = protector.Encrypt(
            string.IsNullOrEmpty(request.Passphrase)
                ? (request.Credential ?? string.Empty)
                : $"{request.Credential}\n---PASSPHRASE---\n{request.Passphrase}");

        var entity = new RemoteHostEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = request.Name.Trim(),
            Host = request.Host.Trim(),
            Port = request.Port is < 1 or > 65535 ? 22 : request.Port,
            Protocol = string.IsNullOrWhiteSpace(request.Protocol) ? "ssh" : request.Protocol.Trim().ToLowerInvariant(),
            AuthType = string.IsNullOrWhiteSpace(request.AuthType) ? "Password" : request.AuthType.Trim(),
            Username = string.IsNullOrWhiteSpace(request.Username) ? "root" : request.Username.Trim(),
            EncryptedCredential = cipher,
            CredentialNonce = nonce,
            CredentialTag = tag,
            HostKeyFingerprint = request.HostKeyFingerprint?.Trim(),
            Tags = request.Tags?.Trim(),
            Remark = request.Remark?.Trim(),
            SortOrder = request.SortOrder,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await store.InsertAsync(entity);
        await operationLogger.LogAsync("添加远程主机", "远程运维", entity.Name, $"{entity.Host}:{entity.Port}", clientIp: HttpContext.GetClientIp());

        var response = new RemoteHostItemResponse(
            Id: entity.Id,
            Name: entity.Name,
            Host: entity.Host,
            Port: entity.Port,
            Protocol: entity.Protocol,
            AuthType: entity.AuthType,
            Username: entity.Username,
            HasCredential: !string.IsNullOrEmpty(entity.EncryptedCredential),
            HostKeyFingerprint: entity.HostKeyFingerprint,
            Tags: entity.Tags,
            Remark: entity.Remark,
            SortOrder: entity.SortOrder,
            CreatedAt: entity.CreatedAt,
            UpdatedAt: entity.UpdatedAt,
            LastConnectedAt: entity.LastConnectedAt
        );

        return Ok(response);
    }

    [HttpPut("{id}")]
    public async Task<IResult> Update(string id, [FromBody] UpdateRemoteHostRequest request)
    {
        var entity = await store.GetByIdAsync(id);
        if (entity is null) return NotFound(new MessageResponse("远程主机不存在"));

        entity.Name = string.IsNullOrWhiteSpace(request.Name) ? entity.Name : request.Name.Trim();
        entity.Host = string.IsNullOrWhiteSpace(request.Host) ? entity.Host : request.Host.Trim();
        entity.Port = request.Port is < 1 or > 65535 ? 22 : request.Port;
        entity.Protocol = string.IsNullOrWhiteSpace(request.Protocol) ? entity.Protocol : request.Protocol.Trim().ToLowerInvariant();
        entity.AuthType = string.IsNullOrWhiteSpace(request.AuthType) ? entity.AuthType : request.AuthType.Trim();
        entity.Username = string.IsNullOrWhiteSpace(request.Username) ? entity.Username : request.Username.Trim();
        entity.HostKeyFingerprint = request.HostKeyFingerprint?.Trim() ?? entity.HostKeyFingerprint;
        entity.Tags = request.Tags?.Trim();
        entity.Remark = request.Remark?.Trim();
        entity.SortOrder = request.SortOrder;
        entity.UpdatedAt = DateTime.UtcNow;

        if (!string.IsNullOrEmpty(request.Credential))
        {
            var (cipher, nonce, tag) = protector.Encrypt(
                string.IsNullOrEmpty(request.Passphrase)
                    ? request.Credential
                    : $"{request.Credential}\n---PASSPHRASE---\n{request.Passphrase}");
            entity.EncryptedCredential = cipher;
            entity.CredentialNonce = nonce;
            entity.CredentialTag = tag;
        }

        await store.UpdateAsync(entity);
        await operationLogger.LogAsync("更新远程主机", "远程运维", entity.Name, $"{entity.Host}:{entity.Port}", clientIp: HttpContext.GetClientIp());

        return Ok(new MessageResponse("更新成功"));
    }

    [HttpDelete("{id}")]
    public async Task<IResult> Delete(string id)
    {
        var entity = await store.GetByIdAsync(id);
        if (entity is null) return NotFound(new MessageResponse("远程主机不存在"));

        await store.DeleteAsync(id);
        await operationLogger.LogAsync("删除远程主机", "远程运维", entity.Name, entity.Host, clientIp: HttpContext.GetClientIp());

        return Ok(new MessageResponse("删除成功"));
    }

    [HttpPost("Test")]
    public async Task<IResult> Test([FromBody] TestRemoteHostRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Host))
        {
            return BadRequest(new MessageResponse("主机地址不能为空"));
        }

        var sw = Stopwatch.StartNew();
        try
        {
            await using var adapter = factory.CreateTransientAdapter(
                host: request.Host.Trim(),
                port: request.Port is < 1 or > 65535 ? 22 : request.Port,
                protocol: request.Protocol,
                authType: request.AuthType,
                username: string.IsNullOrWhiteSpace(request.Username) ? "root" : request.Username.Trim(),
                credential: request.Credential,
                passphrase: request.Passphrase
            );

            var ok = await adapter.TestConnectionAsync(HttpContext.RequestAborted);
            sw.Stop();

            if (ok)
            {
                return Ok(new TestRemoteHostResponse(
                    Success: true,
                    LatencyMs: (int)sw.ElapsedMilliseconds
                ));
            }

            return Ok(new TestRemoteHostResponse(
                Success: false,
                ErrorMessage: "连接或身份认证失败，请检查主机、端口与凭据"
            ));
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Ok(new TestRemoteHostResponse(
                Success: false,
                ErrorMessage: ex.Message
            ));
        }
    }

    [HttpPost("ProbeKey")]
    public async Task<IResult> ProbeKey([FromBody] ProbeRemoteHostKeyRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Host))
        {
            return BadRequest(new MessageResponse("主机地址不能为空"));
        }

        var result = await hostKeyProbe.ProbeAsync(request.Host.Trim(), request.Port, HttpContext.RequestAborted);
        return Ok(new ProbeRemoteHostKeyResponse(
            Success: result.Success,
            Fingerprint: result.FingerprintSha256,
            KeyType: result.KeyType,
            ErrorMessage: result.ErrorMessage
        ));
    }

    [HttpPost("{id}/Terminal/Session")]
    public async Task<IResult> CreateTerminalSession(string id, [FromBody] RemoteTerminalSessionRequest request)
    {
        var entity = await store.GetByIdAsync(id);
        if (entity is null) return NotFound(new MessageResponse("远程主机不存在"));

        try
        {
            var adapter = factory.CreateAdapter(entity);
            var session = await adapter.OpenTerminalAsync(request.Columns, request.Rows, HttpContext.RequestAborted);
            terminalSessions.RegisterSession(session);
            await store.TouchConnectedAsync(id);

            return Ok(new RemoteTerminalSessionResponse(
                SessionId: session.SessionId,
                HostId: entity.Id,
                HostName: entity.Name
            ));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "建立远程交互终端失败: {HostId}", id);
            return StatusCode(500, new MessageResponse($"建立远程终端会话失败: {ex.Message}"));
        }
    }

    [HttpDelete("{id}/Terminal/Session/{sessionId}")]
    public async Task<IResult> CloseTerminalSession(string id, string sessionId)
    {
        var closed = await terminalSessions.CloseSessionAsync(sessionId);
        return Ok(new MessageResponse(closed ? "会话已关闭" : "会话已不存在"));
    }

    [HttpPost("{id}/Terminal/Exec")]
    public async Task<IResult> ExecCommand(string id, [FromBody] RemoteCommandExecRequest request)
    {
        var entity = await store.GetByIdAsync(id);
        if (entity is null) return NotFound(new MessageResponse("远程主机不存在"));

        if (string.IsNullOrWhiteSpace(request.Command))
        {
            return BadRequest(new MessageResponse("执行命令不能为空"));
        }

        try
        {
            await using var adapter = factory.CreateAdapter(entity);
            var timeout = TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 1, 300));
            var result = await adapter.ExecuteCommandAsync(request.Command, timeout, HttpContext.RequestAborted);
            await store.TouchConnectedAsync(id);
            return Ok(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "远程执行命令异常: {HostId}, Cmd={Cmd}", id, request.Command);
            return StatusCode(500, new MessageResponse($"远程执行命令失败: {ex.Message}"));
        }
    }

    [HttpGet("{id}/Files")]
    public async Task<IResult> ListFiles(string id, [FromQuery] string? path = "/")
    {
        var entity = await store.GetByIdAsync(id);
        if (entity is null) return NotFound(new MessageResponse("远程主机不存在"));

        var targetPath = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();
        try
        {
            await using var adapter = factory.CreateAdapter(entity);
            await using var fs = await adapter.OpenFileSystemAsync(HttpContext.RequestAborted);
            var entries = await fs.ListDirectoryAsync(targetPath, HttpContext.RequestAborted);
            await store.TouchConnectedAsync(id);

            return Ok(new RemoteFileListResponse(
                CurrentPath: targetPath,
                Entries: entries
            ));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "读取远程文件列表失败: {HostId}, Path={Path}", id, targetPath);
            return StatusCode(500, new MessageResponse($"读取远程目录失败: {ex.Message}"));
        }
    }

    [HttpGet("{id}/Files/Content")]
    public async Task<IResult> GetFileContent(string id, [FromQuery] string path)
    {
        var entity = await store.GetByIdAsync(id);
        if (entity is null) return NotFound(new MessageResponse("远程主机不存在"));

        if (string.IsNullOrWhiteSpace(path)) return BadRequest(new MessageResponse("文件路径不能为空"));

        try
        {
            await using var adapter = factory.CreateAdapter(entity);
            await using var fs = await adapter.OpenFileSystemAsync(HttpContext.RequestAborted);
            var content = await fs.ReadTextAsync(path.Trim(), HttpContext.RequestAborted);

            return Ok(new RemoteFileContentResponse(
                Path: path.Trim(),
                Content: content,
                Size: content.Length
            ));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "读取远程文件内容失败: {HostId}, Path={Path}", id, path);
            return StatusCode(500, new MessageResponse($"读取远程文件失败: {ex.Message}"));
        }
    }

    [HttpPut("{id}/Files/Content")]
    public async Task<IResult> SaveFileContent(string id, [FromBody] SaveRemoteFileContentRequest request)
    {
        var entity = await store.GetByIdAsync(id);
        if (entity is null) return NotFound(new MessageResponse("远程主机不存在"));

        if (string.IsNullOrWhiteSpace(request.Path)) return BadRequest(new MessageResponse("文件路径不能为空"));

        try
        {
            await using var adapter = factory.CreateAdapter(entity);
            await using var fs = await adapter.OpenFileSystemAsync(HttpContext.RequestAborted);
            await fs.WriteTextAsync(request.Path.Trim(), request.Content ?? string.Empty, HttpContext.RequestAborted);
            await operationLogger.LogAsync("编辑远程文件", "远程运维", entity.Name, request.Path, clientIp: HttpContext.GetClientIp());

            return Ok(new MessageResponse("保存成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "保存远程文件内容失败: {HostId}, Path={Path}", id, request.Path);
            return StatusCode(500, new MessageResponse($"保存远程文件失败: {ex.Message}"));
        }
    }

    [HttpPost("{id}/Files/Action")]
    public async Task<IResult> FileAction(string id, [FromBody] RemoteFileActionRequest request)
    {
        var entity = await store.GetByIdAsync(id);
        if (entity is null) return NotFound(new MessageResponse("远程主机不存在"));

        if (string.IsNullOrWhiteSpace(request.Path)) return BadRequest(new MessageResponse("路径不能为空"));

        try
        {
            await using var adapter = factory.CreateAdapter(entity);
            await using var fs = await adapter.OpenFileSystemAsync(HttpContext.RequestAborted);

            switch (request.Action?.ToLowerInvariant())
            {
                case "mkdir":
                    await fs.CreateDirectoryAsync(request.Path.Trim(), HttpContext.RequestAborted);
                    await operationLogger.LogAsync("新建远程目录", "远程运维", entity.Name, request.Path, clientIp: HttpContext.GetClientIp());
                    return Ok(new MessageResponse("创建目录成功"));

                case "delete":
                    await fs.DeleteAsync(request.Path.Trim(), request.Recursive, HttpContext.RequestAborted);
                    await operationLogger.LogAsync("删除远程文件", "远程运维", entity.Name, request.Path, clientIp: HttpContext.GetClientIp());
                    return Ok(new MessageResponse("删除成功"));

                case "rename":
                    if (string.IsNullOrWhiteSpace(request.Destination))
                    {
                        return BadRequest(new MessageResponse("目标路径不能为空"));
                    }
                    await fs.RenameAsync(request.Path.Trim(), request.Destination.Trim(), HttpContext.RequestAborted);
                    await operationLogger.LogAsync("重命名远程文件", "远程运维", entity.Name, $"{request.Path} -> {request.Destination}", clientIp: HttpContext.GetClientIp());
                    return Ok(new MessageResponse("重命名成功"));

                default:
                    return BadRequest(new MessageResponse($"未知的文件动作: {request.Action}"));
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "远程文件操作失败: {HostId}, Action={Action}, Path={Path}", id, request.Action, request.Path);
            return StatusCode(500, new MessageResponse($"文件操作失败: {ex.Message}"));
        }
    }
}
