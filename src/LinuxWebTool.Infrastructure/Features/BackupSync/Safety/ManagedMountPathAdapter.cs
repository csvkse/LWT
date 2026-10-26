using LinuxWebTool.Contracts.Features.Mount.Contracts;
using LinuxWebTool.Infrastructure.Features.Mount.Adapters;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.BackupSync.Safety;

/// <summary>
/// 受管挂载路径适配器：通过系统级 MountStateMachineService 实时探针与健康快照研判路径状态
/// </summary>
public class ManagedMountPathAdapter(
    MountStateMachineService? mountStateMachine,
    ILogger<ManagedMountPathAdapter> logger) : IPathStorageAdapter
{
    public bool CanHandle(string path)
    {
        return FindMatchingMount(path) is not null;
    }

    public Task<PathHealthReport> InspectAsync(string path, PathRole role, CancellationToken ct = default)
    {
        var matched = FindMatchingMount(path);
        if (matched is null)
        {
            return Task.FromResult(PathHealthReport.Healthy());
        }

        logger.LogDebug("路径 {Path} 匹配到受管挂载点：{MountPath} ({Backend})，状态：{State}",
            path, matched.LocalPath, matched.Backend, matched.State);

        return Task.FromResult(matched.State switch
        {
            MountHealthState.Healthy => PathHealthReport.Healthy(matched.LocalPath, matched.Backend),
            MountHealthState.ServerUnreachable => PathHealthReport.Unhealthy(
                $"关联网络挂载远端不可达 (ServerUnreachable)：{matched.LastError ?? "远程服务端口未响应或网络脱机"}",
                matched.LocalPath, matched.Backend, matched.State),
            MountHealthState.Stale => PathHealthReport.Unhealthy(
                $"关联挂载会话失效或已被非CIFS系统占用 (Stale)：{matched.LastError ?? "文件系统无响应"}",
                matched.LocalPath, matched.Backend, matched.State),
            MountHealthState.NotMounted => PathHealthReport.Unhealthy(
                $"关联网络存储尚未挂载 (NotMounted)，无法读取或写入数据",
                matched.LocalPath, matched.Backend, matched.State),
            MountHealthState.RecoveryFailed => PathHealthReport.Unhealthy(
                $"关联网络挂载自动重挂失败 (RecoveryFailed)：{matched.LastError ?? "挂载重试已达上限并处于退避"}",
                matched.LocalPath, matched.Backend, matched.State),
            MountHealthState.Recovering => PathHealthReport.Unhealthy(
                "关联网络挂载正在尝试自动恢复中 (Recovering)，已暂时阻止任务执行以防并发冲突",
                matched.LocalPath, matched.Backend, matched.State),
            MountHealthState.Unsupported => PathHealthReport.Unhealthy(
                "当前系统环境不支持该挂载类型",
                matched.LocalPath, matched.Backend, matched.State),
            _ => PathHealthReport.Unhealthy(
                $"关联挂载处于异常或未确认状态：{matched.State} ({matched.LastError})",
                matched.LocalPath, matched.Backend, matched.State)
        });
    }

    public MountHealthSnapshot? FindMatchingMount(string path)
    {
        if (mountStateMachine is null || string.IsNullOrWhiteSpace(path)) return null;

        var snapshots = mountStateMachine.GetSnapshots();
        if (snapshots.Count == 0) return null;

        var normalizedTarget = NormalizePath(path);

        return snapshots
            .Where(s => !string.IsNullOrWhiteSpace(s.LocalPath) && IsSubpathOrEqual(s.LocalPath, normalizedTarget))
            .OrderByDescending(s => s.LocalPath.Length)
            .FirstOrDefault();
    }

    private static bool IsSubpathOrEqual(string root, string path)
    {
        var r = NormalizePath(root);
        if (string.Equals(r, path, StringComparison.OrdinalIgnoreCase)) return true;
        if (!r.EndsWith('/')) r += "/";
        return path.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string p)
    {
        return p.Replace('\\', '/').Trim().TrimEnd('/');
    }
}
