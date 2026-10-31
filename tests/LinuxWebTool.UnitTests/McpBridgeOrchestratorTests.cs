using LinuxWebTool.Application.Features.McpBridge;
using LinuxWebTool.Contracts.Features.McpBridge.Contracts;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Contracts.Features.Workers.Contracts;
using Xunit;

namespace LinuxWebTool.UnitTests;

public sealed class McpBridgeOrchestratorTests
{
    private sealed class StubWorkersManager : IWorkersManager
    {
        public WorkerInstanceDto? WorkerToReturn { get; set; }

        public ValueTask<WorkerInstanceDto?> GetByIdAsync(string id, CancellationToken ct = default) =>
            ValueTask.FromResult(WorkerToReturn);

        public ValueTask<IReadOnlyList<WorkerInstanceDto>> GetAllAsync(CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<WorkerInstanceDto>>([]);
        public ValueTask<bool> StartAsync(string id, CancellationToken ct = default) => ValueTask.FromResult(true);
        public ValueTask<(bool Success, string? ErrorMessage)> StartWithResultAsync(string id, CancellationToken ct = default) => ValueTask.FromResult((true, (string?)null));
        public ValueTask<bool> StopAsync(string id, CancellationToken ct = default) => ValueTask.FromResult(true);
        public ValueTask<bool> RestartAsync(string id, CancellationToken ct = default) => ValueTask.FromResult(true);
        public ValueTask<bool> DeleteAsync(string id, CancellationToken ct = default) => ValueTask.FromResult(true);
        public ValueTask<WorkerInstanceDto> CreateOrUpdateAsync(WorkerCreateOrUpdateRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<IReadOnlyList<string>> GetLogsAsync(string id, int limit = 200, CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<string>>([]);
        public ValueTask<WorkerSqliteInfo> GetSqliteInfoAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<IReadOnlyList<WorkerKvItem>> GetKvListAsync(string id, CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<WorkerKvItem>>([]);
        public ValueTask<bool> SetKvAsync(string id, WorkerKvItem item, CancellationToken ct = default) => ValueTask.FromResult(true);
        public ValueTask<bool> DeleteKvAsync(string id, string key, CancellationToken ct = default) => ValueTask.FromResult(true);
        public ValueTask<WorkerRepoScanResult> ScanRepoAsync(string repoUrl, string? targetType = null, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<int> ApplyRepoModulesAsync(WorkerRepoApplyRequest request, CancellationToken ct = default) => ValueTask.FromResult(0);
        public ValueTask<WorkerInstanceDto> ImportZipAsync(Stream zipStream, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<bool> SetEnabledAsync(string id, bool enabled, CancellationToken ct = default) => ValueTask.FromResult(true);
        public ValueTask<WorkerUpdateCheckResponse> CheckUpdateAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<WorkerHotUpdateResponse> HotUpdateAsync(string id, WorkerHotUpdateRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<WorkerSourceMetadata?> GetSourceMetadataAsync(string id, CancellationToken ct = default) => ValueTask.FromResult<WorkerSourceMetadata?>(null);
        public ValueTask<WorkerCodeDto> GetCodeAsync(string id, string? filePath = null, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<bool> SaveCodeAsync(string id, WorkerCodeSaveRequest request, CancellationToken ct = default) => ValueTask.FromResult(true);
        public ValueTask<WorkerDebugRunResultDto> DebugRunAsync(string id, WorkerDebugRunRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<WorkerInstanceDto> DeploySingleFileAsync(WorkerQuickDeployRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class StubControlActions : IMcpBridgeControlActions
    {
        public bool GetStatusCalled { get; set; }
        public bool SaveTunnelCalled { get; set; }

        public ValueTask<McpBridgeStatusDto> GetStatusAsync(ResidentControlSpec spec, string runId, CancellationToken ct)
        {
            GetStatusCalled = true;
            return ValueTask.FromResult(new McpBridgeStatusDto
            {
                WorkerRunId = runId,
                ProcessAlive = true,
                AdminReady = true,
                TunnelState = "connected",
                BackendCount = 2
            });
        }

        public ValueTask<McpBridgeMutationResult> SaveTunnelAsync(ResidentControlSpec spec, string runId, SaveTunnelRequest request, CancellationToken ct)
        {
            SaveTunnelCalled = true;
            return ValueTask.FromResult(new McpBridgeMutationResult { Success = true, Revision = 1 });
        }

        public ValueTask<McpBridgeOperationResult> StartTunnelAsync(ResidentControlSpec spec, string runId, CancellationToken ct) =>
            ValueTask.FromResult(new McpBridgeOperationResult { Success = true });
        public ValueTask<McpBridgeOperationResult> StopTunnelAsync(ResidentControlSpec spec, string runId, CancellationToken ct) =>
            ValueTask.FromResult(new McpBridgeOperationResult { Success = true });
        public ValueTask<McpBridgeServersResult> GetServersAsync(ResidentControlSpec spec, string runId, CancellationToken ct) =>
            ValueTask.FromResult(new McpBridgeServersResult());
        public ValueTask<McpBridgeMutationResult> SaveServerAsync(ResidentControlSpec spec, string runId, string id, SaveServerRequest request, CancellationToken ct) =>
            ValueTask.FromResult(new McpBridgeMutationResult { Success = true });
        public ValueTask<McpBridgeMutationResult> DeleteServerAsync(ResidentControlSpec spec, string runId, string id, ulong expectedRevision, CancellationToken ct) =>
            ValueTask.FromResult(new McpBridgeMutationResult { Success = true });
        public ValueTask<McpBridgeProbeResult> ProbeAsync(ResidentControlSpec spec, string runId, string id, CancellationToken ct) =>
            ValueTask.FromResult(new McpBridgeProbeResult { Success = true, Status = "connected", ToolCount = 5 });
        public ValueTask<McpBridgeDiscoveryResult> DiscoverAsync(ResidentControlSpec spec, string runId, string id, CancellationToken ct) =>
            ValueTask.FromResult(new McpBridgeDiscoveryResult { Success = true, DiscoveredCount = 5 });
        public ValueTask<McpBridgeToolsPage> GetToolsAsync(ResidentControlSpec spec, string runId, string id, string? cursor, CancellationToken ct) =>
            ValueTask.FromResult(new McpBridgeToolsPage());
        public ValueTask<McpBridgeMutationResult> SaveSelectionAsync(ResidentControlSpec spec, string runId, string id, SaveSelectionRequest request, CancellationToken ct) =>
            ValueTask.FromResult(new McpBridgeMutationResult { Success = true });
        public ValueTask<McpBridgeApplyResult> ApplyAsync(ResidentControlSpec spec, string runId, ApplyConfigRequest request, CancellationToken ct) =>
            ValueTask.FromResult(new McpBridgeApplyResult { Success = true, ActiveRevision = 1, ActiveToolCount = 5 });
        public ValueTask<McpBridgeAuditPage> GetAuditAsync(ResidentControlSpec spec, string runId, string? cursor, int limit, CancellationToken ct) =>
            ValueTask.FromResult(new McpBridgeAuditPage());
    }

    [Fact]
    public async Task GetStatusAsync_WorkerNotInstalled_ReturnsStoppedWithFriendlyError()
    {
        var wm = new StubWorkersManager { WorkerToReturn = null };
        var actions = new StubControlActions();
        var orch = new McpBridgeOrchestrator(wm, actions);

        var status = await orch.GetStatusAsync(CancellationToken.None);

        Assert.False(status.ProcessAlive);
        Assert.False(status.AdminReady);
        Assert.Equal("stopped", status.TunnelState);
        Assert.Contains("尚未部署或安装", status.LastError);
        Assert.False(actions.GetStatusCalled);
    }

    [Fact]
    public async Task GetStatusAsync_WorkerNotRunning_ReturnsStoppedError()
    {
        var wm = new StubWorkersManager
        {
            WorkerToReturn = new WorkerInstanceDto
            {
                Id = "mcp-bridge",
                Name = "MCP Bridge",
                State = WorkerState.Stopped,
                Runtime = WorkerRuntimeType.Native
            }
        };
        var actions = new StubControlActions();
        var orch = new McpBridgeOrchestrator(wm, actions);

        var status = await orch.GetStatusAsync(CancellationToken.None);

        Assert.False(status.ProcessAlive);
        Assert.Contains("当前未处于运行状态", status.LastError);
    }

    [Fact]
    public async Task GetStatusAsync_WorkerRunningWithControl_DelegatesToControlActions()
    {
        var wm = new StubWorkersManager
        {
            WorkerToReturn = new WorkerInstanceDto
            {
                Id = "mcp-bridge",
                Name = "MCP Bridge",
                State = WorkerState.Running,
                Runtime = WorkerRuntimeType.Native,
                ProcessId = 999,
                BoundPort = 18888
            }
        };
        var actions = new StubControlActions();
        var orch = new McpBridgeOrchestrator(wm, actions);

        var status = await orch.GetStatusAsync(CancellationToken.None);

        Assert.True(status.ProcessAlive);
        Assert.True(status.AdminReady);
        Assert.Equal("connected", status.TunnelState);
        Assert.Equal("run-999", status.WorkerRunId);
        Assert.True(actions.GetStatusCalled);
    }
}
