using System.IO;
using WinNewsWire.Parsers;

namespace WinNewsWire.Parsers.Tests;

internal static class TestResources
{
    public static string ResourcesDir => Path.Combine(AppContext.BaseDirectory, "Resources");

    public static ParserData Load(string fileName, string url = "https://example.com/feed")
    {
        var path = Path.Combine(ResourcesDir, fileName);
        var bytes = File.ReadAllBytes(path);
        return new ParserData(url, bytes);
    }
}
