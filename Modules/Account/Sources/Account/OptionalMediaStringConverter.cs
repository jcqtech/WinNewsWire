using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinNewsWire.Account;

internal sealed class OptionalMediaStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return reader.GetString();
        using var ignored = JsonDocument.ParseValue(ref reader);
        Trace.TraceWarning("Ignored a non-string optional remote article media field.");
        return null;
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}
