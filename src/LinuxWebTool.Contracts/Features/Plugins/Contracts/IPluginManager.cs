namespace LinuxWebTool.Contracts.Features.Plugins.Contracts;

public interface IPluginManager
{
    ValueTask<IReadOnlyList<PluginInstanceDto>> GetAllAsync(PluginProfile? profileFilter = null, CancellationToken ct = default);
    ValueTask<PluginInstanceDto?> GetByIdAsync(string id, CancellationToken ct = default);
    ValueTask<bool> StartAsync(string id, CancellationToken ct = default);
    ValueTask<(bool Success, string? ErrorMessage)> StartWithResultAsync(string id, CancellationToken ct = default);
    ValueTask<bool> StopAsync(string id, CancellationToken ct = default);
    ValueTask<bool> RestartAsync(string id, CancellationToken ct = default);
    ValueTask<bool> DeleteAsync(string id, CancellationToken ct = default);
    ValueTask<PluginInstanceDto> CreateOrUpdateAsync(PluginCreateOrUpdateRequest request, CancellationToken ct = default);
    ValueTask<IReadOnlyList<string>> GetLogsAsync(string id, int limit = 200, CancellationToken ct = default);
    ValueTask<PluginSqliteInfo> GetSqliteInfoAsync(string id, CancellationToken ct = default);
    ValueTask<IReadOnlyList<PluginKvItem>> GetKvListAsync(string id, CancellationToken ct = default);
    ValueTask<bool> SetKvAsync(string id, PluginKvItem item, CancellationToken ct = default);
    ValueTask<bool> DeleteKvAsync(string id, string key, CancellationToken ct = default);
    ValueTask<PluginRepoScanResult> ScanRepoAsync(string repoUrl, CancellationToken ct = default);
    ValueTask<PluginBatchImportResult> ApplyRepoModulesAsync(PluginRepoApplyRequest request, CancellationToken ct = default);
    ValueTask<PluginInstanceDto> ImportZipAsync(Stream zipStream, CancellationToken ct = default);
    ValueTask<bool> SetEnabledAsync(string id, bool enabled, CancellationToken ct = default);
    ValueTask<PluginSourceMetadata?> GetSourceMetadataAsync(string id, CancellationToken ct = default);
    ValueTask<PluginCodeDto> GetCodeAsync(string id, string? filePath = null, CancellationToken ct = default);
    ValueTask<bool> SaveCodeAsync(string id, PluginCodeSaveRequest request, CancellationToken ct = default);
    ValueTask<PluginDebugRunResultDto> DebugRunAsync(string id, PluginDebugRunRequest request, CancellationToken ct = default);
    string? ResolvePluginAssetPath(string id, string? relativePath);
    ValueTask<PluginActionResult> ExecuteActionAsync(PluginActionRequest request, CancellationToken ct = default);
}
