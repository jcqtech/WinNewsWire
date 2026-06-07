using System.Text.Json;

namespace WinNewsWire.AppShared.ArticleThemes;

/// <summary>Port of Swift <c>ArticleTheme</c>. A <c>.nnwtheme</c> is a folder (or zip expanded
/// to a folder) containing <c>Info.plist</c>/<c>Info.json</c>, <c>template.html</c> and
/// <c>stylesheet.css</c>. We accept either classic Apple plist XML or a JSON sidecar so
/// authors on Windows can ship themes without needing plutil.</summary>
public sealed class ArticleTheme
{
    public const string TemplateFilename = "template.html";
    public const string StylesheetFilename = "stylesheet.css";
    public const string InfoFilenamePlist = "Info.plist";
    public const string InfoFilenameJson = "Info.json";
    public const string ThemeExtension = ".nnwtheme";

    public const string DefaultThemeName = "Default";

    public string Path { get; }
    public bool IsAppTheme { get; }

    public string Name { get; init; } = DefaultThemeName;
    public string CreatorHomePage { get; init; } = "";
    public string CreatorName { get; init; } = "";
    public string Version { get; init; } = "1";
    public string ThemeIdentifier { get; init; } = "com.winnewswire.default";

    public string TemplateHtml { get; init; } = "";
    public string StylesheetCss { get; init; } = "";

    public ArticleTheme(string path, bool isAppTheme)
    {
        Path = path;
        IsAppTheme = isAppTheme;
    }

    /// <summary>Load a theme from a folder (already unpacked). Returns null if required files are missing.</summary>
    public static ArticleTheme? LoadFromFolder(string folder, bool isAppTheme)
    {
        try
        {
            var template = System.IO.Path.Combine(folder, TemplateFilename);
            var stylesheet = System.IO.Path.Combine(folder, StylesheetFilename);
            if (!File.Exists(template) || !File.Exists(stylesheet)) return null;

            var info = TryReadInfo(folder);
            return new ArticleTheme(folder, isAppTheme)
            {
                Name = info.Name ?? System.IO.Path.GetFileNameWithoutExtension(folder),
                CreatorHomePage = info.CreatorHomePage ?? "",
                CreatorName = info.CreatorName ?? "",
                Version = info.Version ?? "1",
                ThemeIdentifier = info.ThemeIdentifier ?? ("com.user." + System.IO.Path.GetFileNameWithoutExtension(folder)),
                TemplateHtml = File.ReadAllText(template),
                StylesheetCss = File.ReadAllText(stylesheet),
            };
        }
        catch { return null; }
    }

    public record InfoDict(string? ThemeIdentifier, string? Name, string? CreatorHomePage, string? CreatorName, string? Version);

    public static InfoDict TryReadInfo(string folder)
    {
        var json = System.IO.Path.Combine(folder, InfoFilenameJson);
        if (File.Exists(json))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(json));
                var r = doc.RootElement;
                return new InfoDict(
                    r.TryGetProperty("ThemeIdentifier", out var ti) ? ti.GetString() : null,
                    r.TryGetProperty("Name", out var n) ? n.GetString() : null,
                    r.TryGetProperty("CreatorHomePage", out var chp) ? chp.GetString() : null,
                    r.TryGetProperty("CreatorName", out var cn) ? cn.GetString() : null,
                    r.TryGetProperty("Version", out var v) ? v.GetString() : null);
            }
            catch { }
        }

        var plist = System.IO.Path.Combine(folder, InfoFilenamePlist);
        if (File.Exists(plist)) return ReadAppleXmlPlist(plist);

        return new InfoDict(null, null, null, null, null);
    }

    /// <summary>Minimal Apple plist XML reader — just enough for the five keys we care about.</summary>
    private static InfoDict ReadAppleXmlPlist(string path)
    {
        try
        {
            var xd = System.Xml.Linq.XDocument.Load(path);
            var dict = xd.Descendants("dict").FirstOrDefault();
            if (dict is null) return new InfoDict(null, null, null, null, null);

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            System.Xml.Linq.XElement? key = null;
            foreach (var el in dict.Elements())
            {
                if (el.Name == "key") key = el;
                else if (key is not null)
                {
                    map[key.Value] = el.Value;
                    key = null;
                }
            }
            map.TryGetValue("ThemeIdentifier", out var ti);
            map.TryGetValue("Name", out var n);
            map.TryGetValue("CreatorHomePage", out var chp);
            map.TryGetValue("CreatorName", out var cn);
            map.TryGetValue("Version", out var v);
            return new InfoDict(ti, n, chp, cn, v);
        }
        catch { return new InfoDict(null, null, null, null, null); }
    }
}
