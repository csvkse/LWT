using LinuxWebTool.Contracts.Models;
using LinuxWebTool.Infrastructure.Mount;
using LinuxWebTool.Infrastructure.Persistence;
using LinuxWebTool.WebHost.Extensions;

namespace LinuxWebTool.WebHost.Routes;

/// <summary>SMB 挂载管理：配置 CRUD、挂载 / 卸载与实时状态。</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SmbMountsController(
    SmbMountStore mountStore,
    WebDavMountStore webDavStore,
    RcloneMountStore rcloneStore,
    SmbMountService mountService,
    MountStateMachineService mountHealth,
    MountBackendCatalog catalog,
    MountOperationCoordinator coordinator,
    IOperationLogger operationLogger) : MinimalApi.ControllerBase
{
    /// <summary>当前环境是否支持挂载管理（Windows 开发机为 false，UI 据此显示提示）。</summary>
    [HttpGet("Support")]
    public IResult Support()
    {
        return Ok(new SmbSupportResponse(
            SmbMountService.IsSupported,
            SmbMountService.IsSupported
                ? string.Empty
                : "当前系统不支持挂载管理（需 Linux）。Docker 部署请以 --privileged --user root 运行（镜像含 cifs-utils）"));
    }

    [HttpGet]
    public async Task<IResult> GetAll()
    {
        var mounts = await mountStore.GetAllAsync();
        var items = mounts.Select(m =>
        {
            var status = mountHealth.GetCachedStatus("smb", m.Id);
            return new SmbMountItemResponse(m.Id, m.Name, m.Server, m.LocalPath, m.Username, m.Domain, m.Options, m.AutoMount, m.Enabled, m.Description, !string.IsNullOrEmpty(m.Password), (int)status, StatusText(status), m.CreateTime, m.UpdateTime, mountHealth.GetSnapshot(m.LocalPath));
        });
        return Ok(items.ToList());
    }

    [HttpPost]
    public async Task<IResult> Create([FromBody] SaveSmbMountRequest request)
    {
        var (valid, message, server, localPath) = Validate(request);
        if (!valid)
        {
            return BadRequest(new MessageResponse(message));
        }
        if (await mountStore.ExistsNameAsync(request.Name.Trim(), null))
        {
            return BadRequest(new MessageResponse("挂载名称已存在"));
        }
        if (await mountStore.ExistsLocalPathAsync(localPath, null))
        {
            return BadRequest(new MessageResponse("本地挂载点已被其他配置使用"));
        }
        if (await webDavStore.ExistsLocalPathAsync(localPath, null))
            return BadRequest(new MessageResponse("本地挂载点已被 WebDAV 配置使用"));
        if (await rcloneStore.ExistsLocalPathAsync(localPath, null))
            return BadRequest(new MessageResponse("本地挂载点已被 SFTP/S3 配置使用"));

        var mount = new SmbMount
        {
            Name = request.Name.Trim(),
            Server = server,
            LocalPath = localPath,
            Username = request.Username?.Trim(),
            Password = request.Password,
            Domain = request.Domain?.Trim(),
            Options = request.Options?.Trim(),
            AutoMount = request.AutoMount,
            Enabled = request.Enabled,
            Description = request.Description?.Trim(),
        };
        await mountStore.InsertAsync(mount);
        mountService.EnsureCredentialFile(mount);
        if (mount.Enabled) SystemStatusProvider.ManagedMountPoints[localPath.Trim().TrimEnd('/')] = 0;
        var desc = await catalog.LoadAsync(new("smb", mount.Id));
        if (desc is not null) mountHealth.ConfigurationChanged(desc);
        await operationLogger.LogAsync("新增挂载配置", "SMB挂载", mount.Name,
            $"{mount.Server} → {mount.LocalPath}", clientIp: HttpContext.GetClientIp());
        return Ok(new IdResponse(mount.Id));
    }

    [HttpPut("{id:guid}")]
    public async Task<IResult> Update(Guid id, [FromBody] SaveSmbMountRequest request)
    {
        return await coordinator.RunWithConfigurationLockAsync("smb", id, async () =>
        {
        var mount = await mountStore.GetByIdAsync(id);
        if (mount is null)
        {
            return NotFound(new MessageResponse("挂载配置不存在"));
        }

        var (valid, message, server, localPath) = Validate(request);
        if (!valid)
        {
            return BadRequest(new MessageResponse(message));
        }
        if (await mountStore.ExistsNameAsync(request.Name.Trim(), id))
        {
            return BadRequest(new MessageResponse("挂载名称已存在"));
        }
        if (await mountStore.ExistsLocalPathAsync(localPath, id))
        {
            return BadRequest(new MessageResponse("本地挂载点已被其他配置使用"));
        }
        if (await webDavStore.ExistsLocalPathAsync(localPath, null))
            return BadRequest(new MessageResponse("本地挂载点已被 WebDAV 配置使用"));
        if (await rcloneStore.ExistsLocalPathAsync(localPath, null))
            return BadRequest(new MessageResponse("本地挂载点已被 SFTP/S3 配置使用"));

        var oldPath = mount.LocalPath;
        var connectionChanged = oldPath != localPath || mount.Server != server || mount.Username != request.Username?.Trim()
            || mount.Domain != request.Domain?.Trim() || mount.Options != request.Options?.Trim()
            || !string.IsNullOrEmpty(request.Password) && mount.Password != request.Password;
        if (connectionChanged && mountService.GetStatus(mount) == SmbMountStatus.Mounted)
            return BadRequest(new MessageResponse("请先卸载，再修改 SMB 配置"));
        mount.Name = request.Name.Trim();
        mount.Server = server;
        mount.LocalPath = localPath;
        mount.Username = request.Username?.Trim();
        if (!string.IsNullOrEmpty(request.Password))
        {
            mount.Password = request.Password; // 留空 = 保留原密码
        }
        mount.Domain = request.Domain?.Trim();
        mount.Options = request.Options?.Trim();
        mount.AutoMount = request.AutoMount;
        mount.Enabled = request.Enabled;
        mount.Description = request.Description?.Trim();
        await mountStore.UpdateAsync(mount);
        mountService.EnsureCredentialFile(mount);
        SystemStatusProvider.ManagedMountPoints[localPath.Trim().TrimEnd('/')] = 0;
        if (oldPath != localPath)
        {
            SystemStatusProvider.ManagedMountPoints.TryRemove(oldPath.Trim().TrimEnd('/'), out _);
        }
        if (await catalog.LoadAsync(new("smb", id)) is { } descriptor) mountHealth.ConfigurationChanged(descriptor);
        await operationLogger.LogAsync("修改挂载配置", "SMB挂载", mount.Name,
            $"{mount.Server} → {mount.LocalPath}", clientIp: HttpContext.GetClientIp());
        return Ok(new IdResponse(mount.Id));
        });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IResult> Delete(Guid id)
    {
        var task = await mountHealth.SubmitAsync("smb", id, "Delete", lazy: true, clientIp: HttpContext.GetClientIp());
        return task is null ? NotFound(new MessageResponse("挂载配置不存在")) : StatusCode(202, task);
    }

    /// <summary>执行挂载。</summary>
    [HttpPost("{id:guid}/Mount")]
    public async Task<IResult> Mount(Guid id)
    {
        var task = await mountHealth.SubmitAsync("smb", id, "Mount", clientIp: HttpContext.GetClientIp());
        return task is null ? NotFound(new MessageResponse("挂载配置不存在")) : StatusCode(202, task);
    }

    /// <summary>执行卸载（body {lazy:true} 懒卸载）。</summary>
    [HttpPost("{id:guid}/Unmount")]
    public async Task<IResult> Unmount(Guid id, [FromBody] UnmountRequest? request = null)
    {
        var task = await mountHealth.SubmitAsync("smb", id, "Unmount", request?.Lazy == true, clientIp: HttpContext.GetClientIp());
        return task is null ? NotFound(new MessageResponse("挂载配置不存在")) : StatusCode(202, task);
    }

    public sealed record UnmountRequest(bool Lazy);

    private static (bool Valid, string Message, string Server, string LocalPath) Validate(SaveSmbMountRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return (false, "名称不能为空", string.Empty, string.Empty);
        }
        if (request.Name.Trim().Length > 100)
        {
            return (false, "名称不能超过 100 个字符", string.Empty, string.Empty);
        }

        // 归一化服务器地址：\\nas\share / nas/share → //nas/share
        var server = request.Server?.Trim().Replace('\\', '/').TrimEnd('/') ?? string.Empty;
        if (server.Length == 0)
        {
            return (false, "服务器地址不能为空", string.Empty, string.Empty);
        }
        if (!server.StartsWith("//", StringComparison.Ordinal))
        {
            server = "//" + server.TrimStart('/');
        }
        if (server.Length < 3)
        {
            return (false, "服务器地址不合法（应为 //主机/共享名）", string.Empty, string.Empty);
        }

        var localPath = request.LocalPath?.Trim() ?? string.Empty;
        if (localPath.Length == 0)
        {
            return (false, "本地挂载点不能为空", string.Empty, string.Empty);
        }
        if (!localPath.StartsWith('/'))
        {
            return (false, "本地挂载点必须为 Linux 绝对路径（如 /mnt/media）", string.Empty, string.Empty);
        }
        if (localPath == "/")
        {
            return (false, "挂载点不能为根目录 /", string.Empty, string.Empty);
        }

        return (true, string.Empty, server, localPath.TrimEnd('/'));
    }

    private static string StatusText(SmbMountStatus status) => status switch
    {
        SmbMountStatus.NotMounted => "未挂载",
        SmbMountStatus.Mounted => "已挂载",
        SmbMountStatus.Abnormal => "异常占用",
        SmbMountStatus.Unsupported => "当前系统不支持",
        _ => "未知",
    };

}
