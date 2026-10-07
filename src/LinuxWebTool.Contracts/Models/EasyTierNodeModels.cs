namespace LinuxWebTool.Contracts.Models;

/// <summary>EasyTier 节点配置传输对象</summary>
public sealed record EasyTierNodeConfigDto(
    string Id,
    string InstanceName,
    string NetworkName,
    string NetworkSecret,
    string? VirtualIpv4,
    bool EnableDhcp,
    List<string> Listeners,
    List<string> Peers,
    List<string> ProxyNetworks,
    List<string> Routes,
    string? RawTomlOverride,
    bool AutoStart,
    int Status,
    string? LastError,
    DateTime UpdateTime);

/// <summary>创建 EasyTier 节点请求契约</summary>
public sealed record CreateEasyTierNodeRequest(
    string InstanceName,
    string NetworkName,
    string? NetworkSecret,
    string? VirtualIpv4,
    bool EnableDhcp,
    List<string>? Listeners,
    List<string>? Peers,
    List<string>? ProxyNetworks,
    List<string>? Routes,
    string? RawTomlOverride,
    bool AutoStart);

/// <summary>更新 EasyTier 节点请求契约</summary>
public sealed record UpdateEasyTierNodeRequest(
    string InstanceName,
    string NetworkName,
    string? NetworkSecret,
    string? VirtualIpv4,
    bool EnableDhcp,
    List<string>? Listeners,
    List<string>? Peers,
    List<string>? ProxyNetworks,
    List<string>? Routes,
    string? RawTomlOverride,
    bool AutoStart);

/// <summary>EasyTier 节点概览与健康状态</summary>
public sealed record EasyTierNodeStatusDto(
    string Id,
    string InstanceName,
    string NetworkName,
    bool IsRunning,
    int Status,
    string? VirtualIpv4,
    string? DeviceName,
    int PeerCount,
    int DirectPeerCount,
    long TotalRxBytes,
    long TotalTxBytes,
    string? LastError,
    DateTime? StartedAt,
    DateTime UpdateTime);

/// <summary>对端 Peer 详情</summary>
public sealed record EasyTierPeerDetailDto(
    string PeerId,
    string Hostname,
    string VirtualIpv4,
    string ConnectionType,
    string Protocol,
    string? TunnelAddress,
    double LatencyMs,
    double LossRate,
    long RxBytes,
    long TxBytes,
    string? NatType = null,
    string? Version = null);

/// <summary>虚拟网络路由项详情</summary>
public sealed record EasyTierRouteDetailDto(
    string DestinationCidr,
    string NextHopPeerId,
    int Cost,
    string PathLatencyMs);

/// <summary>EasyTier 节点完整明细与拓扑</summary>
public sealed record EasyTierNodeDetailDto(
    EasyTierNodeConfigDto Config,
    EasyTierNodeStatusDto Status,
    List<EasyTierPeerDetailDto> Peers,
    List<EasyTierRouteDetailDto> Routes,
    string? StunNatType,
    List<string> LocalPhysicalIps,
    List<string> ActiveListeners,
    string GeneratedToml);

/// <summary>运行时配置热打补丁请求（无需断网重连）</summary>
public sealed record EasyTierPatchRequestDto(
    List<string>? ConnectorsToAdd,
    List<string>? ConnectorsToRemove,
    List<string>? ProxyNetworks,
    List<string>? Routes,
    string? Hostname,
    bool? DisableRelayData,
    bool? PreferPeerRelay);

/// <summary>运行时热补丁结果</summary>
public sealed record EasyTierPatchResultDto(
    bool Success,
    bool RequiredRestart,
    string Message,
    DateTime AppliedAt);

/// <summary>EasyTier 内核引擎宿主运行状态</summary>
public sealed record EasyTierEngineStatusDto(
    bool IsInstalled,
    bool IsRunning,
    string Version,
    string Mode,
    int ProcessId,
    string NativeLibraryPath,
    string StorageDirectory,
    int ActiveNodeCount,
    DateTime? StartTime,
    string? LastError,
    bool HasAdminPrivilege = true,
    string? PrivilegeWarning = null);

/// <summary>内核升级或文件替换执行结果</summary>
public sealed record EasyTierUpgradeResultDto(
    bool Success,
    string PreviousVersion,
    string NewVersion,
    string Message,
    int RestoredNodeCount,
    DateTime ExecutedAt);

/// <summary>GitHub 官方 Release 信息查询响应</summary>
public sealed record EasyTierGitHubReleaseInfoDto(
    string TagName,
    string Name,
    DateTime PublishedAt,
    string Body,
    string MatchingAssetFileName,
    string MatchingAssetDownloadUrl,
    long MatchingAssetSize,
    string CurrentVersion,
    bool HasUpdate);

/// <summary>从 GitHub 自动安装/更新内核请求</summary>
public sealed record InstallGitHubReleaseRequest(
    string? TagName,
    string? ProxyPrefix);
