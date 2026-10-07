using System.Text.Json.Serialization;
namespace LinuxWebTool.Infrastructure.Features.EasyTier.Adapters;

[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(string[]))]
internal partial class EasyTierJsonContext : JsonSerializerContext
{
}
