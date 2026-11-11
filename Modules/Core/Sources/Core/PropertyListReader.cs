using System.Diagnostics;
using System.Xml;
using Claunia.PropertyList;

namespace WinNewsWire.Core;

public static class PropertyListReader
{
    public static Dictionary<string, object>? ReadDictionary(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return null;
        try
        {
            var root = PropertyListParser.Parse(File.ReadAllBytes(path));
            return Convert(root, 0) as Dictionary<string, object>
                ?? throw new InvalidDataException("The settings property list must contain a dictionary.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException
            or PropertyListException or PropertyListFormatException or FormatException
            or ArgumentException or IndexOutOfRangeException)
        {
            Trace.TraceError($"Could not read property list {path}: {ex.Message}");
            return null;
        }
    }

    private static object Convert(NSObject value, int depth)
    {
        if (depth > 128) throw new InvalidDataException("The settings property list is nested too deeply.");
        return value switch
        {
            NSDictionary dictionary => dictionary.ToDictionary(pair => pair.Key, pair => Convert(pair.Value, depth + 1), StringComparer.Ordinal),
            NSArray array => array.Select(item => Convert(item, depth + 1)).ToArray(),
            NSDate date => date.Date.ToUniversalTime(),
            NSString or NSNumber or NSData => value.ToObject(),
            _ => throw new InvalidDataException("The settings property list contains an unsupported value."),
        };
    }
}
