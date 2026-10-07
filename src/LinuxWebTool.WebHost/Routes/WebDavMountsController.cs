using LinuxWebTool.Contracts.Models;
using LinuxWebTool.Infrastructure.Mount;
using LinuxWebTool.Infrastructure.Persistence;
using LinuxWebTool.Infrastructure.Persistence.Entities;
using LinuxWebTool.Infrastructure.SystemInfo;
using LinuxWebTool.Infrastructure.Support;
using LinuxWebTool.WebHost.Extensions;

namespace LinuxWebTool.WebHost.Routes;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class WebDavMountsController(
    WebDavMountStore store,
    SmbMountStore smbStore,
    RcloneMountStore rcloneStore,
    WebDavMountService operations,
    MountStateMachineService health,
    MountBackendCatalog catalog,
    MountOperationCoordinator coordinator,
    DataPaths dataPaths,
    IOperationLogger operationLogger) : MinimalApi.ControllerBase
{
    [HttpGet("Support")]
    public IResult Support() => Ok(new SmbSupportResponse(WebDavMountService.IsSupported,
        WebDavMountService.IsSupported ? string.Empty : "WebDAV 挂载需要 Linux、rclone、/dev/fuse 和容器挂载权限"));

    [HttpGet]
    public async Task<IResult> GetAll()
    {
        var mounts = await store.GetAllAsync();
        return Ok(mounts.Select(m =>
        {
            var status = health.GetCachedStatus("webdav", m.Id);
            return new WebDavMountItemResponse(m.Id, m.Name, m.Url, m.LocalPath, m.Username,
                m.AutoMount, m.Enabled, m.Description, !string.IsNullOrEmpty(m.Password),
                (int)status, StatusText(status), m.CreateTime, m.UpdateTime, health.GetSnapshot(m.LocalPath));
        }).ToList());
    }

    [HttpPost]
    public async Task<IResult> Create([FromBody] SaveWebDavMountRequest request)
    {
        var validation = Validate(request);
        if (validation.Error is not null) return BadRequest(new MessageResponse(validation.Error));
        if (IsProtected(validation.Path)) return BadRequest(new MessageResponse("程序数据目录及其父目录不能作为挂载点"));
        if (await store.ExistsNameAsync(request.Name.Trim(), null)) return BadRequest(new MessageResponse("挂载名称已存在"));
        if (await PathInUseAsync(validation.Path, null)) return BadRequest(new MessageResponse("本地挂载点已被其他配置使用"));
        var mount = new WebDavMount
        {
            Name = request.Name.Trim(), Url = validation.Url, LocalPath = validation.Path,
            Username = request.Username?.Trim(), Password = request.Password,
            AutoMount = request.AutoMount, Enabled = request.Enabled, Description = request.Description?.Trim(),
        };
        await store.InsertAsync(mount);
        if (mount.Enabled) SystemStatusProvider.ManagedMountPoints[mount.LocalPath] = 0;
        if (await catalog.LoadAsync(new("webdav", mount.Id)) is { } descriptor) health.ConfigurationChanged(descriptor);
        await operationLogger.LogAsync("新增挂载配置", "WebDAV挂载", mount.Name,
            $"{mount.Url} → {mount.LocalPath}", clientIp: HttpContext.GetClientIp());
        return Ok(new IdResponse(mount.Id));
    }

    [HttpPut("{id:guid}")]
    public async Task<IResult> Update(Guid id, [FromBody] SaveWebDavMountRequest request)
    {
        return await coordinator.RunWithConfigurationLockAsync("webdav", id, async () =>
        {
        var mount = await store.GetByIdAsync(id);
        if (mount is null) return NotFound(new MessageResponse("挂载配置不存在"));
        var validation = Validate(request);
        if (validation.Error is not null) return BadRequest(new MessageResponse(validation.Error));
        if (IsProtected(validation.Path)) return BadRequest(new MessageResponse("程序数据目录及其父目录不能作为挂载点"));
        if (await store.ExistsNameAsync(request.Name.Trim(), id)) return BadRequest(new MessageResponse("挂载名称已存在"));
        if (await PathInUseAsync(validation.Path, id)) return BadRequest(new MessageResponse("本地挂载点已被其他配置使用"));
        var connectionChanged = mount.LocalPath != validation.Path || mount.Url != validation.Url
            || mount.Username != request.Username?.Trim() || !string.IsNullOrEmpty(request.Password) && mount.Password != request.Password;
        if (connectionChanged && operations.GetStatus(mount) == SmbMountStatus.Mounted)
            return BadRequest(new MessageResponse("请先卸载，再修改 WebDAV 配置"));

        var oldPath = mount.LocalPath;
        mount.Name = request.Name.Trim();
        mount.Url = validation.Url;
        mount.LocalPath = validation.Path;
        mount.Username = request.Username?.Trim();
        if (!string.IsNullOrEmpty(request.Password)) mount.Password = request.Password;
        mount.AutoMount = request.AutoMount;
        mount.Enabled = request.Enabled;
        mount.Description = request.Description?.Trim();
        await store.UpdateAsync(mount);
        if (oldPath != mount.LocalPath || !mount.Enabled)
            SystemStatusProvider.ManagedMountPoints.TryRemove(oldPath, out _);
        if (mount.Enabled) SystemStatusProvider.ManagedMountPoints[mount.LocalPath] = 0;
        if (await catalog.LoadAsync(new("webdav", id)) is { } descriptor) health.ConfigurationChanged(descriptor);
        await operationLogger.LogAsync("修改挂载配置", "WebDAV挂载", mount.Name,
            $"{mount.Url} → {mount.LocalPath}", clientIp: HttpContext.GetClientIp());
        return Ok(new IdResponse(id));
        });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IResult> Delete(Guid id)
    {
        var task = await health.SubmitAsync("webdav", id, "Delete", lazy: false, clientIp: HttpContext.GetClientIp());
        return task is null ? NotFound(new MessageResponse("挂载配置不存在")) : StatusCode(202, task);
    }

    [HttpPost("{id:guid}/Mount")]
    public async Task<IResult> Mount(Guid id)
    {
        var task = await health.SubmitAsync("webdav", id, "Mount", clientIp: HttpContext.GetClientIp());
        return task is null ? NotFound(new MessageResponse("挂载配置不存在")) : StatusCode(202, task);
    }

    [HttpPost("{id:guid}/Unmount")]
    public async Task<IResult> Unmount(Guid id, [FromBody] WebDavUnmountRequest? request = null)
    {
        var task = await health.SubmitAsync("webdav", id, "Unmount", clientIp: HttpContext.GetClientIp());
        return task is null ? NotFound(new MessageResponse("挂载配置不存在")) : StatusCode(202, task);
    }

    public sealed record WebDavUnmountRequest(bool Lazy);

    private async Task<bool> PathInUseAsync(string path, Guid? excludeId) =>
        await store.ExistsLocalPathAsync(path, excludeId) || await smbStore.ExistsLocalPathAsync(path, null)
        || await rcloneStore.ExistsLocalPathAsync(path, null);

    private bool IsProtected(string path)
    {
        var dataRoot = dataPaths.Root.Replace('\\', '/').TrimEnd('/');
        return dataRoot.StartsWith('/') &&
            (path == dataRoot || path.StartsWith(dataRoot + "/", StringComparison.Ordinal)
             || dataRoot.StartsWith(path + "/", StringComparison.Ordinal));
    }

    private static (string? Error, string Url, string Path) Validate(SaveWebDavMountRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 100)
            return ("名称长度须为 1–100 字符", string.Empty, string.Empty);
        if (!Uri.TryCreate(request.Url?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host)
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return ("WebDAV 地址须为不含账号、查询参数的 HTTPS URL", string.Empty, string.Empty);
        var path = request.LocalPath?.Trim().TrimEnd('/') ?? string.Empty;
        if (!path.StartsWith('/') || path.Length < 2
            || path.Split('/').Any(p => p is "." or "..") || path.Any(char.IsControl))
            return ("本地挂载点须为不含 . 或 .. 的 Linux 绝对路径", string.Empty, string.Empty);
        if ((request.Username ?? string.Empty).Any(char.IsControl)
            || (request.Password ?? string.Empty).Any(char.IsControl))
            return ("账号和密码不能包含控制字符", string.Empty, string.Empty);
        if ((request.Username ?? string.Empty).Contains(':'))
            return ("用户名不能包含冒号", string.Empty, string.Empty);
        if (!string.IsNullOrEmpty(request.Password) && string.IsNullOrWhiteSpace(request.Username))
            return ("填写密码时必须同时填写用户名", string.Empty, string.Empty);
        return (null, uri.AbsoluteUri.TrimEnd('/') + "/", path);
    }

    private static string StatusText(SmbMountStatus status) => status switch
    {
        SmbMountStatus.Mounted => "已挂载",
        SmbMountStatus.NotMounted => "未挂载",
        SmbMountStatus.Abnormal => "异常占用",
        _ => "不支持",
    };
}
