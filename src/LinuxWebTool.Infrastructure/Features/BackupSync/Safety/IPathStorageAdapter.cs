namespace LinuxWebTool.Infrastructure.Features.BackupSync.Safety;

/// <summary>
/// 存储路径健康探测适配器接口（适配器模式）
/// 统一适配受管挂载（SMB/WebDAV/Rclone）与本地物理磁盘/未知挂载点的探测差异。
/// </summary>
public interface IPathStorageAdapter
{
    /// <summary>
    /// 该适配器是否负责处理该路径
    /// </summary>
    bool CanHandle(string path);

    /// <summary>
    /// 对指定路径执行健康与可达性检查
    /// </summary>
    Task<PathHealthReport> InspectAsync(string path, PathRole role, CancellationToken ct = default);
}
