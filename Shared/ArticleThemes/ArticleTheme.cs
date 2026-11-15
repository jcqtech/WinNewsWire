using System.Text.Json;
using System.Globalization;
using System.Diagnostics;
using WinNewsWire.Core;

namespace WinNewsWire.AppShared.ArticleThemes;

/// <summary>Port of Swift <c>ArticleTheme</c>. A <c>.nnwtheme</c> is a folder (or zip expanded
/// to a folder) containing <c>Info.plist</c>/<c>Info.json</c>, <c>template.html</c> and
/// <c>stylesheet.css</c>. We accept Apple XML/binary plists or a JSON sidecar so
/// authors on Windows can ship themes without needing plutil.</summary>
public sealed class ArticleTheme
{
    public const string TemplateFilename = "template.html";
    private const string VersionedTemplateFilename = "template7.3.html";
    public const string StylesheetFilename = "stylesheet.css";
    public const string InfoFilenamePlist = "Info.plist";
    public const string InfoFilenameJson = "Info.json";
    public const string ThemeExtension = ".nnwtheme";
    internal const string BuiltInDefaultPath = "(builtin)";

    public const string DefaultThemeName = "Default";

    public string Path { get; }
    public bool IsAppTheme { get; }

    public string Name { get; init; } = DefaultThemeName;
    public string CreatorHomePage { get; init; } = "";
    public string CreatorName { get; init; } = "";
    public string Version { get; init; } = "1";
    public string ThemeIdentifier { get; init; } = "com.winnewswire.default";
    public bool UsesGrayscaleIcon { get; init; }
    internal bool IsBuiltInDefault => Path == BuiltInDefaultPath;

    public string TemplateHtml { get; init; } = "";
    public string StylesheetCss { get; init; } = "";

    public ArticleTheme(string path, bool isAppTheme)
    {
        Path = path;
        IsAppTheme = isAppTheme;
    }

    internal bool HasSameContent(ArticleTheme other)
        => string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase)
            && IsAppTheme == other.IsAppTheme && Name == other.Name
            && CreatorHomePage == other.CreatorHomePage && CreatorName == other.CreatorName
            && Version == other.Version && ThemeIdentifier == other.ThemeIdentifier
            && UsesGrayscaleIcon == other.UsesGrayscaleIcon
            && TemplateHtml == other.TemplateHtml && StylesheetCss == other.StylesheetCss;

    /// <summary>Load a theme from a folder (already unpacked). Returns null if required files are missing.</summary>
    public static ArticleTheme? LoadFromFolder(string folder, bool isAppTheme)
    {
        try
        {
            var template = System.IO.Path.Combine(folder, VersionedTemplateFilename);
            if (!File.Exists(template)) template = System.IO.Path.Combine(folder, TemplateFilename);
            var stylesheet = System.IO.Path.Combine(folder, StylesheetFilename);
            var info = TryReadInfo(folder);
            return new ArticleTheme(folder, isAppTheme)
            {
                Name = System.IO.Path.GetFileNameWithoutExtension(folder),
                CreatorHomePage = info.CreatorHomePage ?? "Unknown",
                CreatorName = info.CreatorName ?? "Unknown",
                Version = info.Version ?? "0",
                ThemeIdentifier = info.ThemeIdentifier ?? ("com.user." + System.IO.Path.GetFileNameWithoutExtension(folder)),
                UsesGrayscaleIcon = info.GrayscaleIcon,
                TemplateHtml = File.Exists(template) ? File.ReadAllText(template) : ReadResource(TemplateFilename),
                StylesheetCss = ReadResource("core.css") + "\n" + (File.Exists(stylesheet) ? File.ReadAllText(stylesheet) : ReadResource(StylesheetFilename)),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            Trace.TraceError($"Could not load article theme {folder}: {ex.Message}");
            return null;
        }
    }

    public record InfoDict(string? ThemeIdentifier, string? Name, string? CreatorHomePage, string? CreatorName, string? Version,
        bool GrayscaleIcon = false);

    private static bool GrayscaleSetting(string? value)
        => value is null ? false : bool.TryParse(value, out var enabled) ? enabled
            : throw new InvalidDataException("The article theme grayscaleIcon setting must be a Boolean.");

    public static InfoDict TryReadInfo(string folder)
    {
        var json = System.IO.Path.Combine(folder, InfoFilenameJson);
        if (File.Exists(json))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(json));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Theme metadata must contain an object.");
            string? Read(string name) => doc.RootElement.EnumerateObject()
                .Where(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                .Select(property => property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.GetRawText())
                .FirstOrDefault();
            return new(Read("themeIdentifier"), Read("name"), Read("creatorHomePage"), Read("creatorName"), Read("version"),
                GrayscaleSetting(Read("grayscaleIcon")));
        }

        var plist = System.IO.Path.Combine(folder, InfoFilenamePlist);
        var values = PropertyListReader.ReadDictionary(plist)
            ?? throw new InvalidDataException("The article theme has no readable Info.plist or Info.json.");
        string? Value(string key) => values.Where(pair => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            .Select(pair => Convert.ToString(pair.Value, CultureInfo.InvariantCulture)).FirstOrDefault();
        return new(Value("themeIdentifier"), Value("name"), Value("creatorHomePage"), Value("creatorName"), Value("version"),
            GrayscaleSetting(Value("grayscaleIcon")));
    }

    internal static string ReadResource(string name)
    {
        var assembly = typeof(ArticleTheme).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetName().Name + ".Resources." + name)
            ?? throw new InvalidDataException($"The built-in article theme resource {name} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
