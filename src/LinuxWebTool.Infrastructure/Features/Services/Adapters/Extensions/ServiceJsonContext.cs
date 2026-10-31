using System.Text.Json.Serialization;
using LinuxWebTool.Contracts.Features.Services.Contracts;

namespace LinuxWebTool.Infrastructure.Features.Services.Adapters.Extensions;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = true)]
[JsonSerializable(typeof(ExtensionSourceMetadata))]
[JsonSerializable(typeof(ExtensionManifest))]
[JsonSerializable(typeof(ExtensionUpdateCheckResponse))]
[JsonSerializable(typeof(ExtensionHotUpdateRequest))]
[JsonSerializable(typeof(ExtensionHotUpdateResponse))]
[JsonSerializable(typeof(ExtensionCodeDto))]
[JsonSerializable(typeof(ExtensionCodeSaveRequest))]
[JsonSerializable(typeof(ExtensionDebugRunRequest))]
[JsonSerializable(typeof(ExtensionCreateRequest))]
[JsonSerializable(typeof(ExtensionDebugRunResultDto))]
[JsonSerializable(typeof(ExtensionHookRequest))]
[JsonSerializable(typeof(ExtensionHookResponse))]
[JsonSerializable(typeof(LinuxWebTool.Contracts.Features.Plugins.Contracts.NativeCommandDefinition))]
[JsonSerializable(typeof(LinuxWebTool.Contracts.Features.Plugins.Contracts.CommandArgumentBinding))]
[JsonSerializable(typeof(LinuxWebTool.Contracts.Features.Plugins.Contracts.CommandStdinBinding))]
[JsonSerializable(typeof(LinuxWebTool.Contracts.Features.Plugins.Contracts.CommandArtifactRule))]
[JsonSerializable(typeof(LinuxWebTool.Contracts.Features.Plugins.Contracts.ResidentControlSpec))]
[JsonSerializable(typeof(LinuxWebTool.Contracts.Features.Plugins.Contracts.PluginActionResult))]
[JsonSerializable(typeof(LinuxWebTool.Contracts.Features.Plugins.Contracts.PluginArtifactRef))]
[JsonSerializable(typeof(LinuxWebTool.Contracts.Features.Plugins.Contracts.PluginOperationDto))]
[JsonSerializable(typeof(LinuxWebTool.Contracts.Features.Plugins.Contracts.OperationAccepted))]
[JsonSerializable(typeof(LinuxWebTool.Contracts.Features.Plugins.Contracts.CommandInvocation))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class ServiceJsonContext : JsonSerializerContext
{
}
