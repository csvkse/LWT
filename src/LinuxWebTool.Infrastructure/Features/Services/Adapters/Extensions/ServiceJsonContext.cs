using System.Text.Json.Serialization;
using LinuxWebTool.Contracts.Features.Services.Contracts;

namespace LinuxWebTool.Infrastructure.Features.Services.Adapters.Extensions;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = true)]
[JsonSerializable(typeof(ExtensionSourceMetadata))]
[JsonSerializable(typeof(ExtensionUpdateCheckResponse))]
[JsonSerializable(typeof(ExtensionHotUpdateRequest))]
[JsonSerializable(typeof(ExtensionHotUpdateResponse))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class ServiceJsonContext : JsonSerializerContext
{
}
