using LinuxWebTool.Contracts.Models;

namespace LinuxWebTool.Contracts.Interfaces;

/// <summary>
/// 系统状态采集器：Linux 读 /proc + df/ps，Windows 降级 P/Invoke 与 DriveInfo。
/// CPU / 网卡速率基于采样窗口差值，实现内部维护上次采样基准。
/// </summary>
public interface ISystemStatusProvider
{
    /// <summary>采集一次即时全量状态。</summary>
    Task<SystemStatusResult> GetStatusAsync(CancellationToken cancellationToken = default);
}
