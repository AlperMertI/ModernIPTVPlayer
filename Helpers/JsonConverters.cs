using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModernIPTVPlayer.Helpers
{
    public class UniversalStringConverter : JsonConverter<string?>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    return reader.GetString();
                case JsonTokenType.Number:
                    if (reader.TryGetInt64(out long l)) return l.ToString();
                    if (reader.TryGetDouble(out double d)) return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return null;
                case JsonTokenType.True:
                    return "true";
                case JsonTokenType.False:
                    return "false";
                case JsonTokenType.Null:
                    return null;
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    return JsonElement.ParseValue(ref reader).GetRawText();
                default:
                    return null;
            }
        }

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        {
            if (value == null) writer.WriteNullValue();
            else writer.WriteStringValue(value);
        }
    }

    public class UniversalStringListConverter : JsonConverter<System.Collections.Generic.List<string>?>
    {
        public override System.Collections.Generic.List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;
            if (reader.TokenType == JsonTokenType.String)
            {
                var s = reader.GetString();
                return string.IsNullOrEmpty(s) ? new System.Collections.Generic.List<string>() : new System.Collections.Generic.List<string> { s };
            }
            if (reader.TokenType == JsonTokenType.StartArray)
            {
                var list = new System.Collections.Generic.List<string>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType == JsonTokenType.String) list.Add(reader.GetString() ?? "");
                    else if (reader.TokenType == JsonTokenType.Number) 
                    {
                        list.Add(JsonElement.ParseValue(ref reader).GetRawText());
                    }
                    else
                    {
                        list.Add(JsonElement.ParseValue(ref reader).GetRawText());
                    }
                }
                return list;
            }
            return null;
        }

        public override void Write(Utf8JsonWriter writer, System.Collections.Generic.List<string>? value, JsonSerializerOptions options)
        {
            if (value == null) writer.WriteNullValue();
            else JsonSerializer.Serialize(writer, value, options);
        }
    }
}
