using System.Globalization;
using System.Text.Json;
using WinNewsWire.Parsers.Xml;

namespace WinNewsWire.Parsers;

internal static class JsonUtilities
{
    public static JsonDocument ParseDictionary(byte[] bytes)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(XmlEncoding.ToUtf8(bytes)); }
        catch (JsonException error) { throw new FeedParserException(FeedParserErrorKind.InvalidJson, error.Message, error); }
        if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
        document.Dispose();
        throw new FeedParserException(FeedParserErrorKind.InvalidJson);
    }

    public static bool TryObjectArray(JsonElement obj, string key, out JsonElement array)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) &&
            value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Object))
        {
            array = value;
            return true;
        }
        array = default;
        return false;
    }

    public static string? GetString(JsonElement obj, string key)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    public static long? GetInt64(JsonElement obj, string key)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) ? Integer(value) : null;

    public static long? Integer(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number) return null;
        if (value.TryGetInt64(out var integer)) return integer;
        return value.TryGetDecimal(out var number) && number == decimal.Truncate(number) && number >= long.MinValue && number <= long.MaxValue
            ? (long)number : null;
    }

    public static string? NumberString(JsonElement value)
    {
        if (Integer(value) is { } integer) return integer.ToString(CultureInfo.InvariantCulture);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number)) return null;
        return DoubleString(number);
    }

    public static string DoubleString(double value)
    {
        var result = value.ToString("R", CultureInfo.InvariantCulture).Replace('E', 'e');
        return result.Contains('.') || result.Contains('e') ? result : result + ".0";
    }

    public static long IntegerValue(string text)
    {
        var value = text.AsSpan().TrimStart();
        var start = value.Length > 0 && value[0] is '+' or '-' ? 1 : 0;
        var end = start;
        while (end < value.Length && char.IsAsciiDigit(value[end])) end++;
        if (end == start) return 0;
        return long.TryParse(value[..end], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
            ? number : value[0] == '-' ? long.MinValue : long.MaxValue;
    }
}
