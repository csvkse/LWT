namespace LinuxWebTool.Contracts.Features.Services.Contracts;

/// <summary>
/// 宿主与服务环境探测器接口。
/// </summary>
public interface IServiceEnvironmentDetector
{
    Task<ServiceEnvironmentInfo> DetectAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Systemd 服务适配器接口（双模：本地原生 Linux + 特权容器 nsenter 宿主穿透 + Windows 降级）。
/// </summary>
public interface ISystemdServiceAdapter
{
    Task<IReadOnlyList<SystemdUnitItem>> ListUnitsAsync(SystemdQueryCriteria criteria, CancellationToken cancellationToken = default);
    Task<ServiceActionResult> ExecuteActionAsync(string unitName, ServiceActionType action, CancellationToken cancellationToken = default);
    Task<SystemdJournalResult> GetJournalAsync(SystemdJournalRequest request, CancellationToken cancellationToken = default);
    Task<string> GetUnitFileContentAsync(string unitName, CancellationToken cancellationToken = default);
}

/// <summary>
/// Docker 引擎适配器接口（Unix Domain Socket / Named Pipe 原生通信）。
/// </summary>
public interface IDockerEngineAdapter
{
    Task<IReadOnlyList<DockerContainerSummary>> ListContainersAsync(bool all = true, CancellationToken cancellationToken = default);
    Task<ServiceActionResult> ExecuteContainerActionAsync(DockerContainerActionRequest request, CancellationToken cancellationToken = default);
    Task<string> GetContainerLogsAsync(DockerLogsRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DockerImageSummary>> ListImagesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 系统级服务配置适配器接口（SSH, Samba, 网络, DNS, 防火墙）。
/// </summary>
public interface ISystemServiceConfigAdapter
{
    Task<SshServiceConfig> GetSshConfigAsync(CancellationToken cancellationToken = default);
    Task<ServiceActionResult> UpdateSshConfigAsync(UpdateSshConfigRequest request, CancellationToken cancellationToken = default);

    Task<SambaServiceConfig> GetSambaConfigAsync(CancellationToken cancellationToken = default);
    Task<ServiceActionResult> UpdateSambaSharesAsync(IReadOnlyList<SambaShareItem> shares, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NetworkInterfaceDetail>> GetNetworkInterfacesAsync(CancellationToken cancellationToken = default);
    Task<DnsResolverConfig> GetDnsConfigAsync(CancellationToken cancellationToken = default);

    Task<FirewallStatusInfo> GetFirewallStatusAsync(CancellationToken cancellationToken = default);
    Task<ServiceActionResult> UpdateFirewallRuleAsync(UpdateFirewallRuleRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// 动态脚本扩展管理器接口。
/// </summary>
public interface IScriptExtensionManager
{
    Task<IReadOnlyList<ExtensionInstance>> ListExtensionsAsync(CancellationToken cancellationToken = default);
    Task<ServiceActionResult> ExecuteExtensionActionAsync(ExtensionActionRequest request, CancellationToken cancellationToken = default);
    Task<string> GetExtensionLogsAsync(string extensionId, int lines = 100, CancellationToken cancellationToken = default);
    Task<InspectRepoModulesResponse> InspectRepositoryModulesAsync(InspectRepoModulesRequest request, CancellationToken cancellationToken = default);
    Task<BatchImportModulesResult> BatchImportModulesAsync(BatchImportModulesRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// 服务管理中枢统一编排器门面。
/// </summary>
public interface IServicesHubOrchestrator
{
    Task<ServiceEnvironmentInfo> GetEnvironmentAsync(CancellationToken cancellationToken = default);
}
