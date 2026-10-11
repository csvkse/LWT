using System.Text.Json.Serialization;
using LinuxWebTool.Contracts.Features.EasyTier.Contracts;
namespace LinuxWebTool.Infrastructure.Features.EasyTier.Adapters;

[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(EasyTierProxyNetworkItem))]
[JsonSerializable(typeof(List<EasyTierProxyNetworkItem>))]
[JsonSerializable(typeof(EasyTierAdvancedConfig))]
internal partial class EasyTierJsonContext : JsonSerializerContext
{
}
