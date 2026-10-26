using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.BackupSync.Safety;

/// <summary>
/// 本地物理文件系统路径健康适配器：负责本地目录存在性、超时防卡死、目标写入权限探针（Canary）与磁盘可用容量采集
/// </summary>
public class LocalFileSystemPathAdapter(ILogger<LocalFileSystemPathAdapter> logger) : IPathStorageAdapter
{
    private const int ProbeTimeoutMs = 3000;

    public bool CanHandle(string path) => true; // 作为默认或兜底适配器

    public async Task<PathHealthReport> InspectAsync(string path, PathRole role, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return PathHealthReport.Unhealthy("路径配置为空");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ProbeTimeoutMs);

        try
        {
            return await Task.Run(() => PerformInspection(path, role, cts.Token), cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("路径健康探测超时（超过 {Ms}ms）：{Path}，疑似存储挂载脱机或内核 I/O 挂起", ProbeTimeoutMs, path);
            return PathHealthReport.Unhealthy($"路径磁盘访问超时（可能底层网络存储断开或内核 I/O 挂死）：{path}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "路径探测异常：{Path}", path);
            return PathHealthReport.Unhealthy($"路径访问异常：{ex.Message}");
        }
    }

    private PathHealthReport PerformInspection(string path, PathRole role, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        long? freeBytes = TryGetAvailableFreeBytes(path);

        if (role == PathRole.Source)
        {
            if (!Directory.Exists(path))
            {
                return PathHealthReport.Unhealthy($"源目录不存在或无法读取：{path}");
            }

            return PathHealthReport.Healthy(freeBytes: freeBytes);
        }

        // Target 角色探测
        try
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            // Canary 写入探针：验证目标路径具备读写权限且不是只读文件系统 (EROFS)
            var canaryFileName = $".lwt_canary_{Guid.NewGuid():N}.tmp";
            var canaryPath = Path.Combine(path, canaryFileName);

            File.WriteAllText(canaryPath, "lwt_safety_probe");
            if (File.Exists(canaryPath))
            {
                File.Delete(canaryPath);
            }

            return PathHealthReport.Healthy(freeBytes: freeBytes);
        }
        catch (UnauthorizedAccessException ex)
        {
            return PathHealthReport.Unhealthy($"目标目录无写权限：{ex.Message}");
        }
        catch (IOException ex)
        {
            return PathHealthReport.Unhealthy($"目标目录写入失败（可能为只读文件系统或存储异常）：{ex.Message}");
        }
        catch (Exception ex)
        {
            return PathHealthReport.Unhealthy($"目标目录准备失败：{ex.Message}");
        }
    }

    private static long? TryGetAvailableFreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady)
                {
                    return drive.AvailableFreeSpace;
                }
            }
        }
        catch
        {
            // 在某些 Linux 虚拟挂载点或网络文件系统上 DriveInfo 可能会异常，忽略并返回 null
        }
        return null;
    }
}
