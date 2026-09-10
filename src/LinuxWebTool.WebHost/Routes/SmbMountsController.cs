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
    SmbMountService mountService,
    IOperationLogger operationLogger) : ControllerBase
{
    /// <summary>当前环境是否支持挂载管理（Windows 开发机为 false，UI 据此显示提示）。</summary>
    [HttpGet("Support")]
    public IActionResult Support()
    {
        return Ok(new
        {
            supported = SmbMountService.IsSupported,
            message = SmbMountService.IsSupported
                ? string.Empty
                : "当前系统不支持挂载管理（需 Linux）。Docker 部署请以 --privileged --user root 运行（镜像含 cifs-utils）",
        });
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var mounts = await mountStore.GetAllAsync();
        var items = mounts.Select(m =>
        {
            var status = mountService.GetStatus(m);
            return new
            {
                m.Id,
                m.Name,
                m.Server,
                m.LocalPath,
                m.Username,
                m.Domain,
                m.Options,
                m.AutoMount,
                m.Enabled,
                m.Description,
                HasPassword = !string.IsNullOrEmpty(m.Password),
                Status = (int)status,
                StatusText = StatusText(status),
                m.CreateTime,
                m.UpdateTime,
            };
        });
        return Ok(items);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveSmbMountRequest request)
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
        await operationLogger.LogAsync("新增挂载配置", "SMB挂载", mount.Name,
            $"{mount.Server} → {mount.LocalPath}", clientIp: HttpContext.GetClientIp());
        return Ok(new IdResponse(mount.Id));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] SaveSmbMountRequest request)
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

        var oldPath = mount.LocalPath;
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
        await operationLogger.LogAsync("修改挂载配置", "SMB挂载", mount.Name,
            $"{mount.Server} → {mount.LocalPath}", clientIp: HttpContext.GetClientIp());
        return Ok(new IdResponse(mount.Id));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var mount = await mountStore.GetByIdAsync(id);
        if (mount is null)
        {
            return NotFound(new MessageResponse("挂载配置不存在"));
        }

        // 挂载中先卸载，避免留下游离挂载点
        var status = mountService.GetStatus(mount);
        if (status == SmbMountStatus.Mounted)
        {
            var (unmounted, message) = await mountService.UnmountAsync(mount, lazy: true);
            if (!unmounted)
            {
                return BadRequest(new MessageResponse($"删除前卸载失败：{message}"));
            }
        }

        await mountStore.DeleteAsync(id);
        mountService.DeleteCredentialFile(id);
        SystemStatusProvider.ManagedMountPoints.TryRemove(mount.LocalPath.Trim().TrimEnd('/'), out _);
        await operationLogger.LogAsync("删除挂载配置", "SMB挂载", mount.Name,
            $"{mount.Server} → {mount.LocalPath}", clientIp: HttpContext.GetClientIp());
        return Ok(new MessageResponse("已删除"));
    }

    /// <summary>执行挂载。</summary>
    [HttpPost("{id:guid}/Mount")]
    public async Task<IActionResult> Mount(Guid id)
    {
        var mount = await mountStore.GetByIdAsync(id);
        if (mount is null)
        {
            return NotFound(new MessageResponse("挂载配置不存在"));
        }

        var (success, message) = await mountService.MountAsync(mount);
        await operationLogger.LogAsync("挂载", "SMB挂载", mount.Name,
            $"{mount.Server} → {mount.LocalPath}", success, clientIp: HttpContext.GetClientIp());
        return success ? Ok(new MessageResponse(message)) : BadRequest(new MessageResponse(message));
    }

    /// <summary>执行卸载（body {lazy:true} 懒卸载）。</summary>
    [HttpPost("{id:guid}/Unmount")]
    public async Task<IActionResult> Unmount(Guid id, [FromBody] UnmountRequest? request)
    {
        var mount = await mountStore.GetByIdAsync(id);
        if (mount is null)
        {
            return NotFound(new MessageResponse("挂载配置不存在"));
        }

        var (success, message) = await mountService.UnmountAsync(mount, request?.Lazy == true);
        await operationLogger.LogAsync("卸载", "SMB挂载", mount.Name,
            $"{mount.Server} → {mount.LocalPath}", success, clientIp: HttpContext.GetClientIp());
        return success ? Ok(new MessageResponse(message)) : BadRequest(new MessageResponse(message));
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
