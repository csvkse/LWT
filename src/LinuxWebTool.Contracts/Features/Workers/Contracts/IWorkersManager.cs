namespace LinuxWebTool.Contracts.Features.Workers.Contracts;

/// <summary>
/// SaaS 函数微服务与长时守护进程管理服务接口。
/// </summary>
public interface IWorkersManager
{
    ValueTask<IReadOnlyList<WorkerInstanceDto>> GetAllAsync(CancellationToken ct = default);
    ValueTask<WorkerInstanceDto?> GetByIdAsync(string id, CancellationToken ct = default);
    ValueTask<bool> StartAsync(string id, CancellationToken ct = default);
    ValueTask<(bool Success, string? ErrorMessage)> StartWithResultAsync(string id, CancellationToken ct = default);
    ValueTask<bool> StopAsync(string id, CancellationToken ct = default);
    ValueTask<bool> RestartAsync(string id, CancellationToken ct = default);
    ValueTask<bool> DeleteAsync(string id, CancellationToken ct = default);
    ValueTask<WorkerInstanceDto> CreateOrUpdateAsync(WorkerCreateOrUpdateRequest request, CancellationToken ct = default);
    ValueTask<IReadOnlyList<string>> GetLogsAsync(string id, int limit = 200, CancellationToken ct = default);
    ValueTask<WorkerSqliteInfo> GetSqliteInfoAsync(string id, CancellationToken ct = default);
    ValueTask<IReadOnlyList<WorkerKvItem>> GetKvListAsync(string id, CancellationToken ct = default);
    ValueTask<bool> SetKvAsync(string id, WorkerKvItem item, CancellationToken ct = default);
    ValueTask<bool> DeleteKvAsync(string id, string key, CancellationToken ct = default);
    ValueTask<WorkerRepoScanResult> ScanRepoAsync(string repoUrl, string? targetType = null, CancellationToken ct = default);
    ValueTask<int> ApplyRepoModulesAsync(WorkerRepoApplyRequest request, CancellationToken ct = default);
    ValueTask<WorkerInstanceDto> ImportZipAsync(Stream zipStream, CancellationToken ct = default);
    ValueTask<bool> SetEnabledAsync(string id, bool enabled, CancellationToken ct = default);
    ValueTask<WorkerUpdateCheckResponse> CheckUpdateAsync(string id, CancellationToken ct = default);
    ValueTask<WorkerHotUpdateResponse> HotUpdateAsync(string id, WorkerHotUpdateRequest request, CancellationToken ct = default);
    ValueTask<WorkerSourceMetadata?> GetSourceMetadataAsync(string id, CancellationToken ct = default);
}
