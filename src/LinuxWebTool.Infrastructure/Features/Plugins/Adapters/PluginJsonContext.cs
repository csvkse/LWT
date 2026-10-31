using System.Text.Json.Serialization;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;

namespace LinuxWebTool.Infrastructure.Features.Plugins.Adapters;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = true)]
[JsonSerializable(typeof(CommandArgumentBinding))]
[JsonSerializable(typeof(CommandStdinBinding))]
[JsonSerializable(typeof(CommandArtifactRule))]
[JsonSerializable(typeof(ResidentControlSpec))]
[JsonSerializable(typeof(NativeCommandDefinition))]
[JsonSerializable(typeof(NativeProgramSpec))]
[JsonSerializable(typeof(PluginArtifactRef))]
[JsonSerializable(typeof(CommandInvocation))]
[JsonSerializable(typeof(OperationAccepted))]
[JsonSerializable(typeof(PluginActionResult))]
[JsonSerializable(typeof(PluginOperationDto))]
[JsonSerializable(typeof(NativeProcessResult))]
[JsonSerializable(typeof(List<PluginOperationDto>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
internal partial class PluginJsonContext : JsonSerializerContext
{
}
