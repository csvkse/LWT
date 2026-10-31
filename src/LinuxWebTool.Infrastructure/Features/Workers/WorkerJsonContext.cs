using System.Text.Json.Serialization;
using LinuxWebTool.Contracts.Features.Workers.Contracts;

namespace LinuxWebTool.Infrastructure.Features.Workers;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = true)]
[JsonSerializable(typeof(WorkerManifest))]
[JsonSerializable(typeof(List<WorkerManifest>))]
[JsonSerializable(typeof(WorkerInstanceDto))]
[JsonSerializable(typeof(List<WorkerInstanceDto>))]
internal partial class WorkerJsonContext : JsonSerializerContext
{
}
