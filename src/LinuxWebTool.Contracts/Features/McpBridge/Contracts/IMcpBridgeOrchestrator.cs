using LinuxWebTool.Contracts.Features.Plugins.Contracts;

namespace LinuxWebTool.Contracts.Features.McpBridge.Contracts;

/// <summary>
/// 宿主端 MCP Bridge 管理编排器接口：
/// - 定位 `mcp-bridge` 常驻服务及其控制接口规格；
/// - 将类型化管理请求转换为固定受控 action，通过控制端点执行；
/// - 提供统一的错误映射、状态检查与权限隔离。
/// </summary>
public interface IMcpBridgeOrchestrator
{
    ValueTask<McpBridgeStatusDto> GetStatusAsync(CancellationToken ct);
    ValueTask<McpBridgeMutationResult> SaveTunnelAsync(SaveTunnelRequest request, CancellationToken ct);
    ValueTask<McpBridgeOperationResult> StartTunnelAsync(CancellationToken ct);
    ValueTask<McpBridgeOperationResult> StopTunnelAsync(CancellationToken ct);
    ValueTask<McpBridgeServersResult> GetServersAsync(CancellationToken ct);
    ValueTask<McpBridgeMutationResult> SaveServerAsync(string id, SaveServerRequest request, CancellationToken ct);
    ValueTask<McpBridgeMutationResult> DeleteServerAsync(string id, ulong expectedRevision, CancellationToken ct);
    ValueTask<McpBridgeProbeResult> ProbeAsync(string id, CancellationToken ct);
    ValueTask<McpBridgeDiscoveryResult> DiscoverAsync(string id, CancellationToken ct);
    ValueTask<McpBridgeToolsPage> GetToolsAsync(string id, string? cursor, CancellationToken ct);
    ValueTask<McpBridgeMutationResult> SaveSelectionAsync(string id, SaveSelectionRequest request, CancellationToken ct);
    ValueTask<McpBridgeApplyResult> ApplyAsync(ApplyConfigRequest request, CancellationToken ct);
    ValueTask<McpBridgeAuditPage> GetAuditAsync(string? cursor, int limit, CancellationToken ct);
}

/// <summary>
/// 将类型化请求调度至 Bridge 内部固定受控 action 的轻量映射接口。
/// </summary>
public interface IMcpBridgeControlActions
{
    ValueTask<McpBridgeStatusDto> GetStatusAsync(ResidentControlSpec spec, string runId, CancellationToken ct);
    ValueTask<McpBridgeMutationResult> SaveTunnelAsync(ResidentControlSpec spec, string runId, SaveTunnelRequest request, CancellationToken ct);
    ValueTask<McpBridgeOperationResult> StartTunnelAsync(ResidentControlSpec spec, string runId, CancellationToken ct);
    ValueTask<McpBridgeOperationResult> StopTunnelAsync(ResidentControlSpec spec, string runId, CancellationToken ct);
    ValueTask<McpBridgeServersResult> GetServersAsync(ResidentControlSpec spec, string runId, CancellationToken ct);
    ValueTask<McpBridgeMutationResult> SaveServerAsync(ResidentControlSpec spec, string runId, string id, SaveServerRequest request, CancellationToken ct);
    ValueTask<McpBridgeMutationResult> DeleteServerAsync(ResidentControlSpec spec, string runId, string id, ulong expectedRevision, CancellationToken ct);
    ValueTask<McpBridgeProbeResult> ProbeAsync(ResidentControlSpec spec, string runId, string id, CancellationToken ct);
    ValueTask<McpBridgeDiscoveryResult> DiscoverAsync(ResidentControlSpec spec, string runId, string id, CancellationToken ct);
    ValueTask<McpBridgeToolsPage> GetToolsAsync(ResidentControlSpec spec, string runId, string id, string? cursor, CancellationToken ct);
    ValueTask<McpBridgeMutationResult> SaveSelectionAsync(ResidentControlSpec spec, string runId, string id, SaveSelectionRequest request, CancellationToken ct);
    ValueTask<McpBridgeApplyResult> ApplyAsync(ResidentControlSpec spec, string runId, ApplyConfigRequest request, CancellationToken ct);
    ValueTask<McpBridgeAuditPage> GetAuditAsync(ResidentControlSpec spec, string runId, string? cursor, int limit, CancellationToken ct);
}
