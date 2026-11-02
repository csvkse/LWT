using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinuxWebTool.Contracts.Features.Plugins.Contracts;

public sealed class PluginDependenciesJsonConverter : JsonConverter<PluginDependencies>
{
    public override PluginDependencies? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var items = ReadDependencyConfigList(ref reader);
            return new PluginDependencies { Items = items };
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var runners = new List<string>();
            var binaries = new List<string>();
            var items = new List<PluginDependencyConfig>();

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                var propName = reader.GetString();
                reader.Read();

                if (string.Equals(propName, "runners", StringComparison.OrdinalIgnoreCase) && reader.TokenType == JsonTokenType.StartArray)
                {
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        if (reader.TokenType == JsonTokenType.String && reader.GetString() is { } r) runners.Add(r);
                    }
                }
                else if (string.Equals(propName, "binaries", StringComparison.OrdinalIgnoreCase) && reader.TokenType == JsonTokenType.StartArray)
                {
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        if (reader.TokenType == JsonTokenType.String && reader.GetString() is { } b) binaries.Add(b);
                    }
                }
                else if (string.Equals(propName, "items", StringComparison.OrdinalIgnoreCase) && reader.TokenType == JsonTokenType.StartArray)
                {
                    items.AddRange(ReadDependencyConfigList(ref reader));
                }
                else
                {
                    reader.Skip();
                }
            }

            return new PluginDependencies { Runners = runners, Binaries = binaries, Items = items };
        }

        throw new JsonException($"解析 PluginDependencies 失败：不支持的 JSON Token 类型 {reader.TokenType}");
    }

    private static List<PluginDependencyConfig> ReadDependencyConfigList(ref Utf8JsonReader reader)
    {
        var list = new List<PluginDependencyConfig>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                var dep = ReadDependencyConfig(ref reader);
                if (dep != null) list.Add(dep);
            }
        }
        return list;
    }

    private static PluginDependencyConfig? ReadDependencyConfig(ref Utf8JsonReader reader)
    {
        string? id = null;
        string? name = null;
        bool required = false;
        bool recommended = true;
        string description = "";
        PluginDependencyProbeConfig? probe = null;
        Dictionary<string, string>? installGuide = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var prop = reader.GetString();
            reader.Read();

            if (string.Equals(prop, "id", StringComparison.OrdinalIgnoreCase))
            {
                id = reader.GetString();
            }
            else if (string.Equals(prop, "name", StringComparison.OrdinalIgnoreCase))
            {
                name = reader.GetString();
            }
            else if (string.Equals(prop, "required", StringComparison.OrdinalIgnoreCase))
            {
                required = reader.GetBoolean();
            }
            else if (string.Equals(prop, "recommended", StringComparison.OrdinalIgnoreCase))
            {
                recommended = reader.GetBoolean();
            }
            else if (string.Equals(prop, "description", StringComparison.OrdinalIgnoreCase))
            {
                description = reader.GetString() ?? "";
            }
            else if (string.Equals(prop, "probe", StringComparison.OrdinalIgnoreCase) && reader.TokenType == JsonTokenType.StartObject)
            {
                probe = ReadProbeConfig(ref reader);
            }
            else if (string.Equals(prop, "installGuide", StringComparison.OrdinalIgnoreCase) && reader.TokenType == JsonTokenType.StartObject)
            {
                installGuide = ReadStringDict(ref reader);
            }
            else
            {
                reader.Skip();
            }
        }

        if (string.IsNullOrEmpty(id)) return null;
        return new PluginDependencyConfig
        {
            Id = id,
            Name = name ?? id,
            Required = required,
            Recommended = recommended,
            Description = description,
            Probe = probe,
            InstallGuide = installGuide
        };
    }

    private static PluginDependencyProbeConfig ReadProbeConfig(ref Utf8JsonReader reader)
    {
        string command = "";
        var args = new List<string>();

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var prop = reader.GetString();
            reader.Read();

            if (string.Equals(prop, "command", StringComparison.OrdinalIgnoreCase))
            {
                command = reader.GetString() ?? "";
            }
            else if ((string.Equals(prop, "args", StringComparison.OrdinalIgnoreCase) || string.Equals(prop, "arguments", StringComparison.OrdinalIgnoreCase)) && reader.TokenType == JsonTokenType.StartArray)
            {
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType == JsonTokenType.String && reader.GetString() is { } a) args.Add(a);
                }
            }
            else
            {
                reader.Skip();
            }
        }

        return new PluginDependencyProbeConfig { Command = command, Args = args };
    }

    private static Dictionary<string, string> ReadStringDict(ref Utf8JsonReader reader)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var key = reader.GetString()!;
            reader.Read();
            if (reader.TokenType == JsonTokenType.String && reader.GetString() is { } val)
            {
                dict[key] = val;
            }
            else
            {
                reader.Skip();
            }
        }
        return dict;
    }

    public override void Write(Utf8JsonWriter writer, PluginDependencies value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.Runners.Count > 0)
        {
            writer.WritePropertyName("runners");
            writer.WriteStartArray();
            foreach (var r in value.Runners) writer.WriteStringValue(r);
            writer.WriteEndArray();
        }
        if (value.Binaries.Count > 0)
        {
            writer.WritePropertyName("binaries");
            writer.WriteStartArray();
            foreach (var b in value.Binaries) writer.WriteStringValue(b);
            writer.WriteEndArray();
        }
        if (value.Items.Count > 0)
        {
            writer.WritePropertyName("items");
            writer.WriteStartArray();
            foreach (var item in value.Items)
            {
                writer.WriteStartObject();
                writer.WriteString("id", item.Id);
                writer.WriteString("name", item.Name);
                writer.WriteBoolean("required", item.Required);
                writer.WriteBoolean("recommended", item.Recommended);
                writer.WriteString("description", item.Description);
                if (item.Probe != null)
                {
                    writer.WritePropertyName("probe");
                    writer.WriteStartObject();
                    writer.WriteString("command", item.Probe.Command);
                    writer.WritePropertyName("args");
                    writer.WriteStartArray();
                    foreach (var a in item.Probe.Args) writer.WriteStringValue(a);
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                if (item.InstallGuide != null)
                {
                    writer.WritePropertyName("installGuide");
                    writer.WriteStartObject();
                    foreach (var (k, v) in item.InstallGuide) writer.WriteString(k, v);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }
}
