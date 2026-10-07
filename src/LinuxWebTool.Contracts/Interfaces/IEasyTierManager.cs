using LinuxWebTool.Contracts.Models;

namespace LinuxWebTool.Contracts.Interfaces;

/// <summary>
/// EasyTier 虚拟组网节点生命周期与双层热更新管理器
/// </summary>
public interface IEasyTierManager
{
    /// <summary>获取所有节点的运行与健康状态</summary>
    Task<IReadOnlyList<EasyTierNodeStatusDto>> GetAllNodeStatusesAsync(CancellationToken ct = default);

    /// <summary>获取指定节点的完整详情与拓扑明细</summary>
    Task<EasyTierNodeDetailDto?> GetNodeDetailAsync(string nodeId, CancellationToken ct = default);

    /// <summary>创建并持久化新网络节点</summary>
    Task<EasyTierNodeStatusDto> CreateNodeAsync(CreateEasyTierNodeRequest request, CancellationToken ct = default);

    /// <summary>更新节点配置（内部智能判断走原地热打补丁还是平滑重启）</summary>
    Task<EasyTierNodeStatusDto> UpdateNodeAsync(string nodeId, UpdateEasyTierNodeRequest request, CancellationToken ct = default);

    /// <summary>删除节点并清理持久化记录</summary>
    Task<bool> DeleteNodeAsync(string nodeId, CancellationToken ct = default);

    /// <summary>启动节点</summary>
    Task<bool> StartNodeAsync(string nodeId, CancellationToken ct = default);

    /// <summary>停止节点</summary>
    Task<bool> StopNodeAsync(string nodeId, CancellationToken ct = default);

    /// <summary>对运行中节点执行原地热打补丁（零丢包、长连接不断）</summary>
    Task<EasyTierPatchResultDto> PatchNodeConfigAsync(string nodeId, EasyTierPatchRequestDto patch, CancellationToken ct = default);

    /// <summary>获取内核引擎宿主当前运行与安装状态</summary>
    Task<EasyTierEngineStatusDto> GetEngineStatusAsync(CancellationToken ct = default);

    /// <summary>执行内核程序平滑热升级（暂存快照 -> 排空旧核 -> 文件替换 -> 自动恢复网络）</summary>
    Task<EasyTierUpgradeResultDto> UpgradeEngineAsync(Stream binaryStream, string fileName, CancellationToken ct = default);

    /// <summary>检查 GitHub 官方 EasyTier Release 最新发布版本信息</summary>
    Task<EasyTierGitHubReleaseInfoDto> CheckGitHubReleaseAsync(string? proxyPrefix = null, CancellationToken ct = default);

    /// <summary>从 GitHub Release 一键下载并热更新内核（支持自动解压并保留多平台依赖）</summary>
    Task<EasyTierUpgradeResultDto> InstallGitHubReleaseAsync(InstallGitHubReleaseRequest request, CancellationToken ct = default);
}
