using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModernIPTVPlayer.Models.Stremio
{
    [Microsoft.UI.Xaml.Data.Bindable]
    public class StremioManifest
    {
        public string Id { get; set; }

        public string Name { get; set; }

        public string Version { get; set; }

        public string Description { get; set; }

        public List<StremioResource> Resources { get; set; }

        public List<string> Types { get; set; }

        public List<StremioCatalog> Catalogs { get; set; } = new();

        public string Logo { get; set; }

        public string Background { get; set; }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    [JsonConverter(typeof(StremioResourceConverter))]
    public class StremioResource
    {
        public string Name { get; set; }

        public List<string> Types { get; set; }

        public List<string> Idprefixes { get; set; }

        public override string ToString() => Name ?? "Unknown Resource";

        public static implicit operator StremioResource(string name) => new StremioResource { Name = name };
    }

    public class StremioResourceConverter : JsonConverter<StremioResource>
    {
        public override StremioResource Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                return new StremioResource { Name = reader.GetString() };
            }
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                var root = JsonElement.ParseValue(ref reader);
                var res = new StremioResource();
                if (root.TryGetProperty("name", out var n)) res.Name = n.GetString();
                if (root.TryGetProperty("types", out var t) && t.ValueKind == JsonValueKind.Array)
                {
                    res.Types = new List<string>();
                    foreach (var item in t.EnumerateArray()) res.Types.Add(item.GetString());
                }
                if (root.TryGetProperty("idPrefixes", out var idp) && idp.ValueKind == JsonValueKind.Array)
                {
                    res.Idprefixes = new List<string>();
                    foreach (var item in idp.EnumerateArray()) res.Idprefixes.Add(item.GetString());
                }
                return res;
            }
            return null;
        }

        public override void Write(Utf8JsonWriter writer, StremioResource value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("name", value.Name);
            if (value.Types != null)
            {
                writer.WriteStartArray("types");
                foreach (var t in value.Types) writer.WriteStringValue(t);
                writer.WriteEndArray();
            }
            if (value.Idprefixes != null)
            {
                writer.WriteStartArray("idPrefixes");
                foreach (var idp in value.Idprefixes) writer.WriteStringValue(idp);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
    }
}
