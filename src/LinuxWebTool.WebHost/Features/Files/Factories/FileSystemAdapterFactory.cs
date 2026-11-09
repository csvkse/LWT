using LinuxWebTool.WebHost.Features.Files.Adapters;

namespace LinuxWebTool.WebHost.Features.Files.Factories;

/// <summary>
/// 文件系统目标适配器工厂：
/// 基于请求携带的目标源（target=container|host）解析对应的底层适配器实例。
/// </summary>
public sealed class FileSystemAdapterFactory(
    ContainerFileSystemAdapter containerAdapter,
    HostPiercingFileSystemAdapter hostAdapter,
    DockerContainerFileSystemAdapter dockerAdapter)
{
    /// <summary>
    /// 解析目标适配器实例（默认为当前容器适配器）
    /// </summary>
    public IFileSystemTargetAdapter GetAdapter(string? target)
    {
        var trimmed = target?.Trim();
        if (string.Equals(trimmed, "host", StringComparison.OrdinalIgnoreCase))
        {
            return hostAdapter;
        }

        if (trimmed != null && trimmed.StartsWith("docker:", StringComparison.OrdinalIgnoreCase))
        {
            return dockerAdapter;
        }

        return containerAdapter;
    }
}
