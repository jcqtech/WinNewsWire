using System.Reflection;

namespace WinNewsWire.Web;

/// <summary>Port of <c>UserAgent</c>.</summary>
public static class UserAgent
{
    public static string Value { get; } = BuildDefault();

    /// <summary>Extended UA string for sites that require more detail (rachelbythebay.com, openrss.org).</summary>
    public static string ExtendedValue { get; } = BuildExtended();

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
