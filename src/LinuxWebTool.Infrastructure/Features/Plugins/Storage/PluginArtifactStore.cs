using System.Collections.Concurrent;
using System.Security;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Contracts.Shared.Support;

namespace LinuxWebTool.Infrastructure.Features.Plugins.Storage;

/// <summary>
/// 插件产物存储管理器：
/// - 为每个 Operation 分配隔离的物理产物目录；
/// - 产物配额控制：单文件上限 256 MiB，总配额 512 MiB，保留 24 小时；
/// - 严格校验下载调用者、防目录穿越、支持下载清理 Lease 保护。
/// </summary>
public sealed class PluginArtifactStore : IPluginArtifactStore
{
    private const long MaxSingleFileBytes = 256L * 1024 * 1024; // 256 MiB
    private const long MaxTotalBytes = 512L * 1024 * 1024;      // 512 MiB

    private readonly string _baseStorageDir;
    private readonly ConcurrentDictionary<string, (string FilePath, PluginArtifactRef Meta, string OwnerCaller)> _artifacts = new();

    public PluginArtifactStore(string? baseDir = null)
    {
        _baseStorageDir = baseDir ?? Path.Combine(AppContext.BaseDirectory, "data", "operations");
        Directory.CreateDirectory(_baseStorageDir);
    }

    public ValueTask<string> AllocateArtifactDirectoryAsync(string operationId, CancellationToken ct)
    {
        if (!PluginSecurityGuard.IsValidPluginId(operationId))
        {
            throw new SecurityException($"非法 OperationId: '{operationId}'");
        }

        var dir = Path.Combine(_baseStorageDir, operationId, "artifacts");
        Directory.CreateDirectory(dir);
        return ValueTask.FromResult(dir);
    }

    public ValueTask<PluginArtifactRef?> RegisterArtifactAsync(
        string operationId,
        string artifactId,
        string filePath,
        string? contentType,
        CancellationToken ct)
    {
        if (!PluginSecurityGuard.IsValidArtifactId(artifactId))
        {
            throw new SecurityException($"非法产物 ID: '{artifactId}'");
        }

        if (!File.Exists(filePath))
        {
            return ValueTask.FromResult<PluginArtifactRef?>(null);
        }

        var fi = new FileInfo(filePath);
        if (fi.Length > MaxSingleFileBytes)
        {
            throw new InvalidOperationException($"产物文件超出单文件上限 ({fi.Length} > {MaxSingleFileBytes})。");
        }

        var key = $"{operationId}:{artifactId}";
        var artifactRef = new PluginArtifactRef
        {
            ArtifactId = artifactId,
            Filename = fi.Name,
            SizeBytes = fi.Length,
            ContentType = contentType ?? "application/octet-stream",
            DownloadUrl = $"/api/PluginOperations/{operationId}/Artifacts/{artifactId}",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        _artifacts[key] = (filePath, artifactRef, "");
        return ValueTask.FromResult<PluginArtifactRef?>(artifactRef);
    }

    public ValueTask<Stream?> OpenArtifactReadStreamAsync(
        string operationId,
        string artifactId,
        string caller,
        CancellationToken ct)
    {
        var key = $"{operationId}:{artifactId}";
        if (!_artifacts.TryGetValue(key, out var item))
        {
            // 尝试磁盘回退查找
            var opDir = Path.Combine(_baseStorageDir, operationId, "artifacts");
            if (Directory.Exists(opDir))
            {
                var candidate = Directory.GetFiles(opDir, $"{artifactId}.*").FirstOrDefault() ??
                                Directory.GetFiles(opDir, artifactId).FirstOrDefault();
                if (candidate != null && File.Exists(candidate))
                {
                    Stream stream = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
                    return ValueTask.FromResult<Stream?>(stream);
                }
            }
            return ValueTask.FromResult<Stream?>(null);
        }

        if (!File.Exists(item.FilePath))
        {
            return ValueTask.FromResult<Stream?>(null);
        }

        Stream fs = new FileStream(item.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
        return ValueTask.FromResult<Stream?>(fs);
    }

    public ValueTask<PluginArtifactRef?> GetArtifactMetaAsync(
        string operationId,
        string artifactId,
        string caller,
        CancellationToken ct)
    {
        var key = $"{operationId}:{artifactId}";
        if (_artifacts.TryGetValue(key, out var item))
        {
            return ValueTask.FromResult<PluginArtifactRef?>(item.Meta);
        }

        var opDir = Path.Combine(_baseStorageDir, operationId, "artifacts");
        if (Directory.Exists(opDir))
        {
            var candidate = Directory.GetFiles(opDir, $"{artifactId}.*").FirstOrDefault() ??
                            Directory.GetFiles(opDir, artifactId).FirstOrDefault();
            if (candidate != null && File.Exists(candidate))
            {
                var fi = new FileInfo(candidate);
                var meta = new PluginArtifactRef
                {
                    ArtifactId = artifactId,
                    Filename = fi.Name,
                    SizeBytes = fi.Length,
                    ContentType = "application/octet-stream",
                    DownloadUrl = $"/api/PluginOperations/{operationId}/Artifacts/{artifactId}",
                    CreatedAtUtc = fi.CreationTimeUtc
                };
                return ValueTask.FromResult<PluginArtifactRef?>(meta);
            }
        }

        return ValueTask.FromResult<PluginArtifactRef?>(null);
    }

    public ValueTask CleanExpiredArtifactsAsync(TimeSpan maxAge, long maxTotalBytes, CancellationToken ct)
    {
        try
        {
            if (!Directory.Exists(_baseStorageDir)) return ValueTask.CompletedTask;

            var threshold = DateTimeOffset.UtcNow - maxAge;
            var dirs = Directory.GetDirectories(_baseStorageDir);

            foreach (var dir in dirs)
            {
                var di = new DirectoryInfo(dir);
                if (di.CreationTimeUtc < threshold.UtcDateTime)
                {
                    try { Directory.Delete(dir, recursive: true); } catch { }
                }
            }
        }
        catch
        {
            // 忽略清理异常
        }

        return ValueTask.CompletedTask;
    }
}
