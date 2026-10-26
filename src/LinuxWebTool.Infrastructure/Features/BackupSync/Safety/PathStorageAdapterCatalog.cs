namespace LinuxWebTool.Infrastructure.Features.BackupSync.Safety;

/// <summary>
/// 存储路径健康探测适配器目录：协调管理各种存储介质适配器并输出综合诊断报告
/// </summary>
public class PathStorageAdapterCatalog(
    ManagedMountPathAdapter managedAdapter,
    LocalFileSystemPathAdapter localAdapter)
{
    public async Task<PathHealthReport> InspectAsync(string path, PathRole role, CancellationToken ct = default)
    {
        // 1. 若匹配到受管挂载（SMB/WebDAV/Rclone），优先判定挂载健康状态机快照
        if (managedAdapter.CanHandle(path))
        {
            var mountReport = await managedAdapter.InspectAsync(path, role, ct);
            if (!mountReport.IsAccessible || !mountReport.IsWritable)
            {
                // 挂载已脱机/异常，绝不继续进行底层文件 I/O 探针（防止线程陷入内核 D 状态挂死）
                return mountReport;
            }

            // 挂载正常，继续进行本地可达性与 Canary 写入验证，并合并挂载元信息
            var localReport = await localAdapter.InspectAsync(path, role, ct);
            return localReport with
            {
                AssociatedMountPath = mountReport.AssociatedMountPath,
                AssociatedBackend = mountReport.AssociatedBackend,
                MountState = mountReport.MountState
            };
        }

        // 2. 本地非受管目录直接探测
        return await localAdapter.InspectAsync(path, role, ct);
    }
}
