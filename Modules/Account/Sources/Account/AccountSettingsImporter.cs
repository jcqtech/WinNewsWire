using WinNewsWire.Web;
using WinNewsWire.Core;

namespace WinNewsWire.Account;

public static class AccountSettingsImporter
{
    public sealed record ImportedSettings(string? Name, bool? IsActive, string? Username,
        DateTime? LastArticleFetchStartTime, DateTime? LastRefreshCompletedDate, Uri? EndpointUrl,
        string? ExternalID, IReadOnlyDictionary<string, ConditionalGetInfo>? ConditionalGetInfo);

    public static ImportedSettings? ReadSettingsFromPlist(string accountID, string dataFolder)
    {
        var values = PropertyListReader.ReadDictionary(Path.Combine(dataFolder, "Settings.plist"));
        if (values is null) return null;
        var conditional = new Dictionary<string, ConditionalGetInfo>(StringComparer.Ordinal);
        if (values.GetValueOrDefault("conditionalGetInfo") is Dictionary<string, object> endpoints)
            foreach (var (endpoint, raw) in endpoints)
                if (raw is Dictionary<string, object> headers)
                {
                    var modified = headers.GetValueOrDefault("lastModified") as string;
                    var etag = headers.GetValueOrDefault("etag") as string;
                    if (modified is not null || etag is not null) conditional[endpoint] = new(modified, etag);
                }
        return new(values.GetValueOrDefault("name") as string, values.GetValueOrDefault("isActive") as bool?,
            values.GetValueOrDefault("username") as string, values.GetValueOrDefault("lastArticleFetch") as DateTime?,
            values.GetValueOrDefault("lastArticleFetchEndTime") as DateTime?, ParseEndpoint(values.GetValueOrDefault("endpointURL")),
            values.GetValueOrDefault("externalID") as string, conditional.Count > 0 ? conditional : null);
    }

    private static Uri? ParseEndpoint(object? value)
    {
        if (value is string text) return Uri.TryCreate(text, UriKind.RelativeOrAbsolute, out var uri) ? uri : null;
        if (value is not Dictionary<string, object> parts) return null;
        var relative = parts.GetValueOrDefault("relative") as string;
        var baseText = parts.GetValueOrDefault("base") as string;
        if (Uri.TryCreate(baseText, UriKind.Absolute, out var baseUri))
            return relative is not null ? new Uri(baseUri, relative) : baseUri;
        return relative is not null && Uri.TryCreate(relative, UriKind.RelativeOrAbsolute, out var result) ? result : null;
    }
}
