using System.Reflection;

namespace WinNewsWire.Web;

public enum UserAgentStyle { Feed, SpecialCaseFeed, Browser }

/// <summary>Port of <c>UserAgent</c>.</summary>
public static class UserAgent
{
    public static string Value { get; } = BuildDefault();

    /// <summary>Extended UA string for sites that require more detail (rachelbythebay.com, openrss.org).</summary>
    public static string ExtendedValue { get; } = BuildExtended();
    public static string BrowserValue { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) WinNewsWire";

    public static Dictionary<string, string>? Headers()
    {
        return new Dictionary<string, string> { ["User-Agent"] = Value };
    }

    private static string BuildDefault()
    {
        var ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        return $"WinNewsWire/{ver} (+https://github.com/ - Windows)";
    }

    private static string BuildExtended()
    {
        var ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        return $"WinNewsWire/{ver} (Windows; https://github.com/) like NetNewsWire (RSS reader)";
    }
}
