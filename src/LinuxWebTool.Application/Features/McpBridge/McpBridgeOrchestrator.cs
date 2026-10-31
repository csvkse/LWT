using LinuxWebTool.Contracts.Features.McpBridge.Contracts;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Contracts.Features.Workers.Contracts;

namespace LinuxWebTool.Application.Features.McpBridge;

/// <summary>
/// 宿主端 MCP Bridge 管理编排器：
/// - 定位 `mcp-bridge` 常驻服务的当前运行代次与控制端点规格；
/// - 拦截未运行、未声明控制或处于非就绪状态时的非法调用，返回结构化 503/未就绪结果；
/// - 保持所有操作针对受信当前代次，防端口复用与跨代次改投。
/// </summary>
public sealed class McpBridgeOrchestrator : IMcpBridgeOrchestrator
{
    public const string BridgeWorkerId = "mcp-bridge";

    private readonly IWorkersManager _workersManager;
    private readonly IMcpBridgeControlActions _controlActions;

    public McpBridgeOrchestrator(
        IWorkersManager workersManager,
        IMcpBridgeControlActions controlActions)
    {
        _workersManager = workersManager ?? throw new ArgumentNullException(nameof(workersManager));
        _controlActions = controlActions ?? throw new ArgumentNullException(nameof(controlActions));
    }

    private async ValueTask<(ResidentControlSpec? Spec, string RunId, string? Error)> ResolveBridgeContextAsync(CancellationToken ct)
    {
        var worker = await _workersManager.GetByIdAsync(BridgeWorkerId, ct).ConfigureAwait(false);
        if (worker == null)
        {
            return (null, "0", $"服务 '{BridgeWorkerId}' 尚未部署或安装。");
        }

        if (worker.State != WorkerState.Running)
        {
            return (null, "0", $"Bridge 服务当前未处于运行状态 (当前状态: {worker.State})，请先启动服务。");
        }

        if (worker.BoundPort is null or <= 0)
        {
            return (null, "0", "Bridge 服务未声明或分配回环端口。");
        }

        var spec = new ResidentControlSpec
        {
            Port = worker.BoundPort,
            BaseUrl = worker.AccessUrl ?? $"http://127.0.0.1:{worker.BoundPort}"
        };

        var runId = worker.ProcessId.HasValue ? $"run-{worker.ProcessId.Value}" : "0";
        return (spec, runId, null);
    }

    public async ValueTask<McpBridgeStatusDto> GetStatusAsync(CancellationToken ct)
    {
        var (spec, runId, error) = await ResolveBridgeContextAsync(ct).ConfigureAwait(false);
        if (error != null || spec == null)
        {
            return new McpBridgeStatusDto
            {
                WorkerRunId = null,
                ProcessAlive = false,
                AdminReady = false,
                TunnelState = "stopped",
                LastError = error
            };
        }

        return await _controlActions.GetStatusAsync(spec, runId, ct).ConfigureAwait(false);
    }

    public async ValueTask<McpBridgeMutationResult> SaveTunnelAsync(SaveTunnelRequest request, CancellationToken ct)
    {
        var (spec, runId, error) = await ResolveBridgeContextAsync(ct).ConfigureAwait(false);
        if (error != null || spec == null) return new McpBridgeMutationResult { Success = false, Message = error };
        return await _controlActions.SaveTunnelAsync(spec, runId, request, ct).ConfigureAwait(false);
    }

    public async ValueTask<McpBridgeOperationResult> StartTunnelAsync(CancellationToken ct)
    {
        var (spec, runId, error) = await ResolveBridgeContextAsync(ct).ConfigureAwait(false);
        if (error != null || spec == null) return new McpBridgeOperationResult { Success = false, Message = error };
        return await _controlActions.StartTunnelAsync(spec, runId, ct).ConfigureAwait(false);
    }

    public async ValueTask<McpBridgeOperationResult> StopTunnelAsync(CancellationToken ct)
    {
        var (spec, runId, error) = await ResolveBridgeContextAsync(ct).ConfigureAwait(false);
        if (error != null || spec == null) return new McpBridgeOperationResult { Success = false, Message = error };
        return await _controlActions.StopTunnelAsync(spec, runId, ct).ConfigureAwait(false);
    }

    public async ValueTask<McpBridgeServersResult> GetServersAsync(CancellationToken ct)
    {
        var (spec, runId, error) = await ResolveBridgeContextAsync(ct).ConfigureAwait(false);
        if (error != null || spec == null) return new McpBridgeServersResult();
        return await _controlActions.GetServersAsync(spec, runId, ct).ConfigureAwait(false);
    }

    public async ValueTask<McpBridgeMutationResult> SaveServerAsync(string id, SaveServerRequest request, CancellationToken ct)
    {
        var (spec, runId, error) = await ResolveBridgeContextAsync(ct).ConfigureAwait(false);
        if (error != null || spec == null) return new McpBridgeMutationResult { Success = false, Message = error };
        return await _controlActions.SaveServerAsync(spec, runId, id, request, ct).ConfigureAwait(false);
    }

    public async ValueTask<McpBridgeMutationResult> DeleteServerAsync(string id, ulong expectedRevision, CancellationToken ct)
    {
        var (spec, runId, error) = await ResolveBridgeContextAsync(ct).ConfigureAwait(false);
        if (error != null || spec == null) return new McpBridgeMutationResult { Success = false, Message = error };
        return await _controlActions.DeleteServerAsync(spec, runId, id, expectedRevision, ct).ConfigureAwait(false);
    }

    public async ValueTask<McpBridgeProbeResult> ProbeAsync(string id, CancellationToken ct)
    {
        var (spec, runId, error) = await ResolveBridgeContextAsync(ct).ConfigureAwait(false);
        if (error != null || spec == null) return new McpBridgeProbeResult { Success = false, Status = "error", Message = error };
        return await _controlActions.ProbeAsync(spec, runId, id, ct).ConfigureAwait(false);
    }

    public async ValueTask<McpBridgeDiscoveryResult> DiscoverAsync(string id, CancellationToken ct)
    {
        var (spec, runId, error) = await ResolveBridgeContextAsync(ct).ConfigureAwait(false);
        if (error != null || spec == null) return new McpBridgeDiscoveryResult { Success = false, Message = error };
        return await _controlActions.DiscoverAsync(spec, runId, id, ct).ConfigureAwait(false);
    }

    public async ValueTask<McpBridgeToolsPage> GetToolsAsync(string id, string? cursor, CancellationToken ct)
    {
        var (spec, runId, error) = await ResolveBridgeContextAsync(ct).ConfigureAwait(false);
        if (error != null || spec == null) return new McpBridgeToolsPage();
        return await _controlActions.GetToolsAsync(spec, runId, id, cursor, ct).ConfigureAwait(false);
    }

    public async ValueTask<McpBridgeMutationResult> SaveSelectionAsync(string id, SaveSelectionRequest request, CancellationToken ct)
    {
        var (spec, runId, error) = await ResolveBridgeContextAsync(ct).ConfigureAwait(false);
        if (error != null || spec == null) return new McpBridgeMutationResult { Success = false, Message = error };
        return await _controlActions.SaveSelectionAsync(spec, runId, id, request, ct).ConfigureAwait(false);
    }

    public async ValueTask<McpBridgeApplyResult> ApplyAsync(ApplyConfigRequest request, CancellationToken ct)
    {
        var (spec, runId, error) = await ResolveBridgeContextAsync(ct).ConfigureAwait(false);
        if (error != null || spec == null) return new McpBridgeApplyResult { Success = false, Message = error };
        return await _controlActions.ApplyAsync(spec, runId, request, ct).ConfigureAwait(false);
    }

    public async ValueTask<McpBridgeAuditPage> GetAuditAsync(string? cursor, int limit, CancellationToken ct)
    {
        var (spec, runId, error) = await ResolveBridgeContextAsync(ct).ConfigureAwait(false);
        if (error != null || spec == null) return new McpBridgeAuditPage();
        return await _controlActions.GetAuditAsync(spec, runId, cursor, limit, ct).ConfigureAwait(false);
    }
}
