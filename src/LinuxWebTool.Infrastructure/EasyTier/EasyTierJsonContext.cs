using System.Text.Json.Serialization;

namespace LinuxWebTool.Infrastructure.EasyTier;

[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(string[]))]
internal partial class EasyTierJsonContext : JsonSerializerContext
{
}
