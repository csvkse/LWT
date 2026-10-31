namespace LinuxWebTool.Contracts.Features.Plugins.Contracts;

public sealed record NativeProcessResult
{
    public bool Started { get; init; }
    public int ExitCode { get; init; } = -1;
    public bool TimedOut { get; init; }
    public bool Cancelled { get; init; }
    public bool EffectUncertain { get; init; }
    public string? StandardOutput { get; init; }
    public string? StandardError { get; init; }
    public string? OutputFile { get; init; }
}

public interface ICommandProgressObserver
{
    ValueTask OnProgressAsync(double? percent, string? message, CancellationToken ct);
}

public interface INativeProgramResolver
{
    string ResolveExecutable(string source, string programRefOrEntrypoint, string packageDir);
}

public interface ICommandBindingResolver
{
    ValueTask<NativeProgramSpec> ResolveAsync(
        NativeCommandDefinition definition,
        CommandInvocation invocation,
        string workingDir,
        string operationId,
        CancellationToken ct);
}

public interface INativeProcessRunner
{
    Task<NativeProcessResult> RunAsync(
        NativeProgramSpec spec,
        ICommandProgressObserver? observer,
        CancellationToken ct);
}

public interface INativeCommandExecutor
{
    Task<PluginActionResult> ExecuteAsync(
        NativeCommandDefinition definition,
        CommandInvocation invocation,
        string workingDir,
        string operationId,
        ICommandProgressObserver? observer,
        CancellationToken ct);
}

public interface IPluginOperationCoordinator
{
    ValueTask<OperationAccepted> SubmitAsync(CommandInvocation invocation, CancellationToken ct);
    ValueTask<PluginOperationDto?> GetAsync(string operationId, string caller, CancellationToken ct);
    ValueTask<PluginOperationDto?> CancelAsync(string operationId, string caller, CancellationToken ct);
}

public interface IPluginOperationStore
{
    ValueTask SaveAsync(PluginOperationDto operation, CancellationToken ct);
    ValueTask<PluginOperationDto?> GetByIdAsync(string operationId, CancellationToken ct);
    ValueTask<PluginOperationDto?> GetByIdempotencyKeyAsync(string caller, string pluginIdentity, string action, string idempotencyKey, CancellationToken ct);
    ValueTask UpdateStatusAsync(string operationId, string status, double? progress, string? progressMessage, PluginActionResult? result, CancellationToken ct);
    ValueTask CleanExpiredRecordsAsync(TimeSpan maxAge, int maxCount, CancellationToken ct);
}

public interface IPluginArtifactStore
{
    ValueTask<string> AllocateArtifactDirectoryAsync(string operationId, CancellationToken ct);
    ValueTask<PluginArtifactRef?> RegisterArtifactAsync(string operationId, string artifactId, string filePath, string? contentType, CancellationToken ct);
    ValueTask<Stream?> OpenArtifactReadStreamAsync(string operationId, string artifactId, string caller, CancellationToken ct);
    ValueTask<PluginArtifactRef?> GetArtifactMetaAsync(string operationId, string artifactId, string caller, CancellationToken ct);
    ValueTask CleanExpiredArtifactsAsync(TimeSpan maxAge, long maxTotalBytes, CancellationToken ct);
}

public interface IResidentControlClient
{
    Task<bool> HandshakeAsync(ResidentControlSpec spec, string runId, CancellationToken ct);
    Task<string?> HealthAsync(ResidentControlSpec spec, string runId, CancellationToken ct);
    Task<PluginActionResult> InvokeAsync(ResidentControlSpec spec, string runId, string action, System.Text.Json.JsonElement payload, CancellationToken ct);
    Task<bool> ShutdownAsync(ResidentControlSpec spec, string runId, TimeSpan timeout, CancellationToken ct);
}
