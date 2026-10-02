using LinuxWebTool.Contracts.Models;
using LinuxWebTool.Infrastructure.Persistence;

namespace LinuxWebTool.Infrastructure.Mount;

public readonly record struct MountKey(string Backend, Guid Id);
public sealed record MountOperationResult(bool Success, MountFailureKind Kind = MountFailureKind.None, string? Error = null)
{
    public static MountOperationResult From((bool Success, string Message) result, MountFailureKind failure = MountFailureKind.Failed) =>
        result.Success ? new(true) : new(false, failure, result.Message);
}

// A descriptor is used only after a fresh store read under the configuration lock.
public sealed record MountDescriptor(MountKey Key, string Name, string Path, DateTime Version, bool Enabled, bool AutoMount,
    bool Supported, Func<SmbMountStatus> Status,
    Func<CancellationToken, Task<MountOperationResult>> Remote,
    Func<CancellationToken, Task<MountOperationResult>> Verify,
    Func<CancellationToken, Task<MountOperationResult>> Mount,
    Func<bool, CancellationToken, Task<MountOperationResult>> Unmount,
    Func<CancellationToken, Task> Delete);

public sealed class MountBackendCatalog(SmbMountStore smbStore, WebDavMountStore webDavStore, RcloneMountStore rcloneStore,
    SmbMountService smb, SmbMountRuntimeProbe smbProbe, WebDavMountService webDav, WebDavMountProbe webDavProbe,
    RcloneMountService rclone)
{
    public async Task<IReadOnlyList<MountDescriptor>> ListAsync()
    {
        var result = new List<MountDescriptor>();
        foreach (var m in await smbStore.GetAllAsync()) result.Add(Smb(m));
        foreach (var m in await webDavStore.GetAllAsync()) result.Add(WebDav(m));
        foreach (var m in await rcloneStore.GetAllAsync()) result.Add(Rclone(m));
        return result;
    }

    public async Task<MountDescriptor?> LoadAsync(MountKey key) => key.Backend switch
    {
        "smb" => await smbStore.GetByIdAsync(key.Id) is { } m ? Smb(m) : null,
        "webdav" => await webDavStore.GetByIdAsync(key.Id) is { } m ? WebDav(m) : null,
        "rclone" => await rcloneStore.GetByIdAsync(key.Id) is { } m ? Rclone(m) : null,
        _ => null,
    };

    private MountDescriptor Smb(Persistence.Entities.SmbMount m) => new(new("smb", m.Id), m.Name, m.LocalPath,
        m.UpdateTime, m.Enabled, m.AutoMount, SmbMountService.HasMountDependencies, () => smb.GetStatus(m),
        async ct => await smbProbe.IsServerReachableAsync(m, ct) ? new(true) : new(false, MountFailureKind.Unreachable, "SMB 服务不可达"),
        async ct =>
        {
            var directory = await smbProbe.IsFileSystemAccessibleAsync(m, ct);
            if (!directory.Success) return new(false, MountFailureKind.Timeout, directory.Error);
            var capacity = await smbProbe.IsCapacityProbeOkAsync(m, ct);
            return new(capacity.Success, capacity.Success ? MountFailureKind.None : MountFailureKind.Timeout, capacity.Error);
        }, async ct => await smb.MountResultAsync(m, ct),
        async (lazy, ct) => MountOperationResult.From(await smb.UnmountAsync(m, lazy, ct), MountFailureKind.Busy),
        async ct => { ct.ThrowIfCancellationRequested(); smb.DeleteCredentialFile(m.Id); await smbStore.DeleteAsync(m.Id); });

    private MountDescriptor WebDav(Persistence.Entities.WebDavMount m) => new(new("webdav", m.Id), m.Name, m.LocalPath,
        m.UpdateTime, m.Enabled, m.AutoMount, WebDavMountService.IsSupported, () => webDav.GetStatus(m),
        async ct =>
        {
            var r = await webDavProbe.ProbeRemoteAsync(m, ct);
            return new(r.Success, r.Success ? MountFailureKind.None : r.CredentialsRejected ? MountFailureKind.AuthenticationFailed : MountFailureKind.Unreachable, r.Error);
        }, async ct =>
        {
            var r = await webDavProbe.ProbeLocalAsync(m, ct);
            return new(r.Success, r.Success ? MountFailureKind.None : MountFailureKind.Timeout, r.Error);
        }, async ct => MountOperationResult.From(await webDav.MountAsync(m, ct)),
        async (_, ct) => MountOperationResult.From(await webDav.UnmountAsync(m, false, ct), MountFailureKind.Busy),
        async ct => { ct.ThrowIfCancellationRequested(); webDav.DeleteConfig(m.Id); await webDavStore.DeleteAsync(m.Id); });

    private MountDescriptor Rclone(Persistence.Entities.RcloneMount m) => new(new("rclone", m.Id), m.Name, m.LocalPath,
        m.UpdateTime, m.Enabled, m.AutoMount, RcloneMountService.IsSupported, () => rclone.GetStatus(m),
        async ct =>
        {
            var r = await rclone.ProbeRemoteAsync(m, ct);
            return new(r.Success, r.Success ? MountFailureKind.None : r.CredentialsRejected ? MountFailureKind.AuthenticationFailed : MountFailureKind.Unreachable, r.Error);
        }, async ct =>
        {
            var r = await rclone.ProbeLocalAsync(m, ct);
            return new(r.Success, r.Success ? MountFailureKind.None : MountFailureKind.Timeout, r.Error);
        }, async ct => MountOperationResult.From(await rclone.MountAsync(m, ct)),
        async (_, ct) => MountOperationResult.From(await rclone.UnmountAsync(m, ct), MountFailureKind.Busy),
        async ct => { ct.ThrowIfCancellationRequested(); rclone.DeleteConfig(m.Id); await rcloneStore.DeleteAsync(m.Id); });
}
