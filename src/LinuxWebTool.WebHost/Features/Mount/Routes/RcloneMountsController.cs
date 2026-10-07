namespace LinuxWebTool.WebHost.Features.Mount.Routes;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class RcloneMountsController(
    RcloneMountStore store,
    SmbMountStore smbStore,
    WebDavMountStore webDavStore,
    RcloneMountService operations,
    MountStateMachineService health,
    MountBackendCatalog catalog,
    MountOperationCoordinator coordinator,
    DataPaths dataPaths,
    IOperationLogger operationLogger) : MinimalApi.ControllerBase
{
    [HttpGet("Support")]
    public IResult Support() => Ok(new SmbSupportResponse(RcloneMountService.IsSupported,
        RcloneMountService.IsSupported ? string.Empty : "SFTP/S3 挂载需要 Linux、rclone、/dev/fuse 和挂载权限"));

    [HttpGet]
    public async Task<IResult> GetAll()
    {
        var mounts = await store.GetAllAsync();
        return Ok(mounts.Select(m =>
        {
            var status = health.GetCachedStatus("rclone", m.Id);
            return new RcloneMountItemResponse(m.Id, m.Name, m.Kind, m.LocalPath, m.RemotePath,
                m.Host, m.Port, m.Username, m.KeyFile, m.HostKey, m.Endpoint, m.Bucket, m.Region,
                m.AccessKeyId, !string.IsNullOrEmpty(m.Password), !string.IsNullOrEmpty(m.SecretAccessKey),
                m.AutoMount, m.Enabled, m.Description, (int)status, StatusText(status),
                m.CreateTime, m.UpdateTime, health.GetSnapshot(m.LocalPath));
        }).ToList());
    }

    [HttpPost]
    public async Task<IResult> Create([FromBody] SaveRcloneMountRequest request)
    {
        var validation = Validate(request, null);
        if (validation.Error is not null) return BadRequest(new MessageResponse(validation.Error));
        if (IsProtected(validation.Path)) return BadRequest(new MessageResponse("程序数据目录及其父目录不能作为挂载点"));
        if (await store.ExistsNameAsync(request.Name.Trim(), null)) return BadRequest(new MessageResponse("挂载名称已存在"));
        if (await PathInUseAsync(validation.Path, null)) return BadRequest(new MessageResponse("本地挂载点已被其他配置使用"));
        var mount = new RcloneMount();
        Assign(mount, request, validation.Path);
        await store.InsertAsync(mount);
        if (mount.Enabled) SystemStatusProvider.ManagedMountPoints[mount.LocalPath] = 0;
        if (await catalog.LoadAsync(new("rclone", mount.Id)) is { } descriptor) health.ConfigurationChanged(descriptor);
        await LogAsync("新增挂载配置", mount, "已保存", true);
        return Ok(new IdResponse(mount.Id));
    }

    [HttpPut("{id:guid}")]
    public async Task<IResult> Update(Guid id, [FromBody] SaveRcloneMountRequest request)
    {
        return await coordinator.RunWithConfigurationLockAsync("rclone", id, async () =>
        {
        var mount = await store.GetByIdAsync(id);
        if (mount is null) return NotFound(new MessageResponse("挂载配置不存在"));
        if (!string.Equals(mount.Kind, request.Kind, StringComparison.Ordinal))
            return BadRequest(new MessageResponse("不能修改挂载类型，请新建配置"));
        var validation = Validate(request, mount);
        if (validation.Error is not null) return BadRequest(new MessageResponse(validation.Error));
        if (IsProtected(validation.Path)) return BadRequest(new MessageResponse("程序数据目录及其父目录不能作为挂载点"));
        if (await store.ExistsNameAsync(request.Name.Trim(), id)) return BadRequest(new MessageResponse("挂载名称已存在"));
        if (await PathInUseAsync(validation.Path, id)) return BadRequest(new MessageResponse("本地挂载点已被其他配置使用"));
        var remotePath = request.Kind == "sftp" ? "/" + (request.RemotePath ?? "").Trim().Trim('/') : (request.RemotePath ?? "").Trim().Trim('/');
        var endpoint = string.IsNullOrWhiteSpace(request.Endpoint) ? null : request.Endpoint.Trim().TrimEnd('/');
        var connectionChanged = mount.LocalPath != validation.Path || mount.RemotePath != remotePath
            || mount.Host != request.Host?.Trim() || mount.Port != request.Port || mount.Username != request.Username?.Trim()
            || mount.KeyFile != request.KeyFile?.Trim() || mount.HostKey != request.HostKey?.Trim()
            || mount.Endpoint != endpoint || mount.Bucket != request.Bucket?.Trim() || mount.Region != request.Region?.Trim()
            || mount.AccessKeyId != request.AccessKeyId?.Trim() || !string.IsNullOrEmpty(request.Password) && mount.Password != request.Password
            || !string.IsNullOrEmpty(request.SecretAccessKey) && mount.SecretAccessKey != request.SecretAccessKey;
        if (connectionChanged && operations.GetStatus(mount) == SmbMountStatus.Mounted)
            return BadRequest(new MessageResponse("请先卸载，再修改配置"));
        var oldPath = mount.LocalPath;
        Assign(mount, request, validation.Path);
        await store.UpdateAsync(mount);
        if (oldPath != mount.LocalPath || !mount.Enabled)
            SystemStatusProvider.ManagedMountPoints.TryRemove(oldPath, out _);
        if (mount.Enabled) SystemStatusProvider.ManagedMountPoints[mount.LocalPath] = 0;
        if (await catalog.LoadAsync(new("rclone", id)) is { } descriptor) health.ConfigurationChanged(descriptor);
        await LogAsync("修改挂载配置", mount, "已保存", true);
        return Ok(new IdResponse(id));
        });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IResult> Delete(Guid id)
    {
        var task = await health.SubmitAsync("rclone", id, "Delete", lazy: false, clientIp: HttpContext.GetClientIp());
        return task is null ? NotFound(new MessageResponse("挂载配置不存在")) : StatusCode(202, task);
    }

    [HttpPost("{id:guid}/Mount")]
    public async Task<IResult> Mount(Guid id)
    {
        var task = await health.SubmitAsync("rclone", id, "Mount", clientIp: HttpContext.GetClientIp());
        return task is null ? NotFound(new MessageResponse("挂载配置不存在")) : StatusCode(202, task);
    }

    [HttpPost("{id:guid}/Unmount")]
    public async Task<IResult> Unmount(Guid id)
    {
        var task = await health.SubmitAsync("rclone", id, "Unmount", clientIp: HttpContext.GetClientIp());
        return task is null ? NotFound(new MessageResponse("挂载配置不存在")) : StatusCode(202, task);
    }

    private Task LogAsync(string action, RcloneMount mount, string detail, bool success) =>
        operationLogger.LogAsync(action, mount.Kind.ToUpperInvariant() + "挂载", mount.Name,
            $"{mount.LocalPath}；{detail}", success, clientIp: HttpContext.GetClientIp());

    private async Task<bool> PathInUseAsync(string path, Guid? excludeId) =>
        await store.ExistsLocalPathAsync(path, excludeId)
        || await smbStore.ExistsLocalPathAsync(path, null)
        || await webDavStore.ExistsLocalPathAsync(path, null);

    private bool IsProtected(string path)
    {
        var dataRoot = dataPaths.Root.Replace('\\', '/').TrimEnd('/');
        return dataRoot.StartsWith('/') &&
            (path == dataRoot || path.StartsWith(dataRoot + "/", StringComparison.Ordinal)
             || dataRoot.StartsWith(path + "/", StringComparison.Ordinal));
    }

    private static void Assign(RcloneMount mount, SaveRcloneMountRequest request, string path)
    {
        mount.Name = request.Name.Trim();
        mount.Kind = request.Kind;
        mount.LocalPath = path;
        mount.RemotePath = request.Kind == "sftp"
            ? "/" + (request.RemotePath ?? string.Empty).Trim().Trim('/')
            : (request.RemotePath ?? string.Empty).Trim().Trim('/');
        mount.Host = request.Host?.Trim();
        mount.Port = request.Port;
        mount.Username = request.Username?.Trim();
        if (!string.IsNullOrEmpty(request.Password)) mount.Password = request.Password;
        mount.KeyFile = request.KeyFile?.Trim();
        mount.HostKey = request.HostKey?.Trim();
        mount.Endpoint = string.IsNullOrWhiteSpace(request.Endpoint) ? null : request.Endpoint.Trim().TrimEnd('/');
        mount.Bucket = request.Bucket?.Trim();
        mount.Region = request.Region?.Trim();
        mount.AccessKeyId = request.AccessKeyId?.Trim();
        if (!string.IsNullOrEmpty(request.SecretAccessKey)) mount.SecretAccessKey = request.SecretAccessKey;
        mount.AutoMount = request.AutoMount;
        mount.Enabled = request.Enabled;
        mount.Description = request.Description?.Trim();
    }

    private static (string? Error, string Path) Validate(SaveRcloneMountRequest request, RcloneMount? existing)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 100)
            return ("名称长度须为 1–100 字符", string.Empty);
        if (request.Kind is not ("sftp" or "s3")) return ("仅支持 SFTP 或 S3", string.Empty);
        var path = request.LocalPath?.Trim().TrimEnd('/') ?? string.Empty;
        if (!SafePath(path)) return ("本地挂载点须为不含 . 或 .. 的 Linux 绝对路径", string.Empty);
        var values = new[] { request.Name, request.RemotePath, request.Host, request.Username, request.Password,
            request.KeyFile, request.HostKey, request.Endpoint, request.Bucket, request.Region,
            request.AccessKeyId, request.SecretAccessKey, request.Description };
        if (values.Any(v => v is not null && v.Any(char.IsControl)))
            return ("配置字段不能包含控制字符", string.Empty);
        if (request.Kind == "sftp")
        {
            if (string.IsNullOrWhiteSpace(request.Host) || request.Host.Length > 255
                || request.Host.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or '@'))
                return ("请输入有效的 SFTP 主机名或 IP", string.Empty);
            if (request.Port is < 1 or > 65535) return ("SFTP 端口须为 1–65535", string.Empty);
            if (string.IsNullOrWhiteSpace(request.Username)) return ("SFTP 用户名不能为空", string.Empty);
            if (string.IsNullOrEmpty(request.Password) && string.IsNullOrEmpty(existing?.Password)
                && string.IsNullOrWhiteSpace(request.KeyFile))
                return ("SFTP 需要密码或容器内私钥文件路径", string.Empty);
            if (!string.IsNullOrWhiteSpace(request.KeyFile) && !SafePath(request.KeyFile.Trim()))
                return ("私钥文件须为容器内 Linux 绝对路径", string.Empty);
            var keyParts = request.HostKey?.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (keyParts is not { Length: 2 } || !keyParts[0].StartsWith("ssh-", StringComparison.Ordinal)
                && !keyParts[0].StartsWith("ecdsa-", StringComparison.Ordinal))
                return ("请填写受信任的 SSH 主机公钥（算法 + Base64 公钥）", string.Empty);
            try { if (Convert.FromBase64String(keyParts[1]).Length < 32) throw new FormatException(); }
            catch (FormatException) { return ("SSH 主机公钥格式不正确", string.Empty); }
            if (!string.IsNullOrWhiteSpace(request.RemotePath) && !SafeRemotePath(request.RemotePath))
                return ("SFTP 远端路径不能包含 . 或 .. 段", string.Empty);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.Bucket) || request.Bucket.Contains('/')
                || request.Bucket.Any(char.IsWhiteSpace)) return ("S3 存储桶名称无效", string.Empty);
            if (string.IsNullOrWhiteSpace(request.AccessKeyId)
                || string.IsNullOrEmpty(request.SecretAccessKey) && string.IsNullOrEmpty(existing?.SecretAccessKey))
                return ("S3 访问密钥和秘密密钥不能为空", string.Empty);
            if (string.IsNullOrWhiteSpace(request.Endpoint))
            {
                if (string.IsNullOrWhiteSpace(request.Region)) return ("AWS S3 需要区域", string.Empty);
            }
            else if (!Uri.TryCreate(request.Endpoint.Trim(), UriKind.Absolute, out var endpoint)
                || endpoint.Scheme != Uri.UriSchemeHttps || endpoint.UserInfo.Length > 0
                || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0 || endpoint.AbsolutePath != "/")
                return ("S3 自定义端点须为不含路径和账号的 HTTPS 地址", string.Empty);
            if (!string.IsNullOrWhiteSpace(request.RemotePath) && !SafeRemotePath(request.RemotePath))
                return ("S3 前缀不能包含 . 或 .. 段", string.Empty);
        }
        return (null, path);
    }

    private static bool SafePath(string path) => path.StartsWith('/') && path.Length > 1
        && !path.Split('/').Any(p => p is "." or "..") && !path.Any(char.IsControl);
    private static bool SafeRemotePath(string path) => !path.Split('/').Any(p => p is "." or "..");

    private static string StatusText(SmbMountStatus status) => status switch
    {
        SmbMountStatus.Mounted => "已挂载", SmbMountStatus.NotMounted => "未挂载",
        SmbMountStatus.Abnormal => "异常占用", _ => "不支持",
    };
}
