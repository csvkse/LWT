using System.Text.Json.Serialization;
using LinuxWebTool.Contracts.Features.Workers.Contracts;

namespace LinuxWebTool.Infrastructure.Features.Workers;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = true)]
[JsonSerializable(typeof(WorkerManifest))]
[JsonSerializable(typeof(List<WorkerManifest>))]
[JsonSerializable(typeof(WorkerInstanceDto))]
[JsonSerializable(typeof(List<WorkerInstanceDto>))]
[JsonSerializable(typeof(WorkerSourceMetadata))]
[JsonSerializable(typeof(WorkerUpdateCheckResponse))]
[JsonSerializable(typeof(WorkerHotUpdateRequest))]
[JsonSerializable(typeof(WorkerHotUpdateResponse))]
[JsonSerializable(typeof(WorkerOperationResponse))]
[JsonSerializable(typeof(WorkerRepoScanResult))]
[JsonSerializable(typeof(WorkerRepoApplyRequest))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class WorkerJsonContext : JsonSerializerContext
{
}
