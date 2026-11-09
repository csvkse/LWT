namespace LinuxWebTool.Contracts.Features.Plugins.Contracts;

public interface IPluginManager
{
    ValueTask<IReadOnlyList<PluginInstanceDto>> GetAllAsync(PluginProfile? profileFilter = null, CancellationToken ct = default);
    ValueTask<PluginInstanceDto?> GetByIdAsync(string id, CancellationToken ct = default);
    ValueTask<bool> StartAsync(string id, CancellationToken ct = default);
    ValueTask<(bool Success, string? ErrorMessage)> StartWithResultAsync(string id, CancellationToken ct = default);
    ValueTask<bool> StopAsync(string id, CancellationToken ct = default);
    ValueTask<bool> StopAsync(string id, bool isManual, CancellationToken ct = default);
    ValueTask<bool> RestartAsync(string id, CancellationToken ct = default);
    ValueTask<bool> DeleteAsync(string id, CancellationToken ct = default);
    ValueTask<PluginInstanceDto> CreateOrUpdateAsync(PluginCreateOrUpdateRequest request, CancellationToken ct = default);
    ValueTask<IReadOnlyList<string>> GetLogsAsync(string id, int limit = 200, CancellationToken ct = default);
    ValueTask<PluginSqliteInfo> GetSqliteInfoAsync(string id, CancellationToken ct = default);
    ValueTask<IReadOnlyList<PluginKvItem>> GetKvListAsync(string id, CancellationToken ct = default);
    ValueTask<PluginKvItem?> GetKvAsync(string id, string key, CancellationToken ct = default);
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
    ValueTask<bool> UploadFileAsync(string id, string relativePath, Stream fileStream, CancellationToken ct = default);
    ValueTask<(Stream Stream, string ContentType, string FileName)> DownloadFileAsync(string id, string relativePath, CancellationToken ct = default);
    ValueTask<bool> DeleteFileAsync(string id, string relativePath, CancellationToken ct = default);
    string? ResolvePluginAssetPath(string id, string? relativePath);
    ValueTask<PluginPortCheckResult> CheckPortAsync(string id, int port, CancellationToken ct = default);
    ValueTask<PluginPortSwitchResult> SwitchPortAsync(string id, int newPort, bool restartIfRunning = true, CancellationToken ct = default);
    ValueTask<PluginActionResult> ExecuteActionAsync(PluginActionRequest request, CancellationToken ct = default);
    ValueTask<IReadOnlyList<string>> ListRevisionsAsync(string id, CancellationToken ct = default);
    ValueTask<bool> SwitchRevisionAliasAsync(string id, string alias, string revisionName, CancellationToken ct = default);
    ValueTask<IReadOnlyList<ServiceDescriptorDto>> DiscoverServicesAsync(CancellationToken ct = default);
    ValueTask<PluginInvokeResponse> InvokeServiceAsync(PluginInvokeRequest request, CancellationToken ct = default);
    ValueTask<PluginBuildResult> BuildPluginAsync(string id, CancellationToken ct = default);
    string? GetInternalSecret(string id);
    event Action? OnPluginStateOrManifestChanged;
}
