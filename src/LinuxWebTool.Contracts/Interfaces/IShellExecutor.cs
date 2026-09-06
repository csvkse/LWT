using LinuxWebTool.Contracts.Models;

namespace LinuxWebTool.Contracts.Interfaces;

/// <summary>
/// Shell 命令执行器。Linux 使用 /bin/bash -c，Windows 开发环境降级 cmd /c。
/// </summary>
public interface IShellExecutor
{
    Task<ShellResult> ExecuteAsync(ShellRequest request, CancellationToken cancellationToken = default);
}
