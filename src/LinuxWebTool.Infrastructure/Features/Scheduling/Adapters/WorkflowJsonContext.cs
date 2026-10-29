using System.Text.Json.Serialization;
using LinuxWebTool.Contracts.Features.Workflows.Contracts;

namespace LinuxWebTool.Infrastructure.Features.Scheduling.Adapters;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(WorkflowDefinition))]
[JsonSerializable(typeof(List<string>))]
public partial class WorkflowJsonContext : JsonSerializerContext
{
}
