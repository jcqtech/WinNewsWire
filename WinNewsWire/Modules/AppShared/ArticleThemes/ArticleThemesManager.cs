using System.IO.Compression;
using WinNewsWire.Core;

namespace WinNewsWire.AppShared.ArticleThemes;

/// <summary>Port of Swift <c>ArticleThemesManager</c>. Scans <c>%LOCALAPPDATA%/WinNewsWire/Themes</c>
/// for <c>.nnwtheme</c> folders (and zips, which get expanded on import), exposes the currently
/// selected theme and raises <see cref="CurrentThemeChanged"/> when it rolls over.</summary>
public sealed class ArticleThemesManager
{
    public static ArticleThemesManager Shared { get; } = new();

    public event EventHandler? CurrentThemeChanged;

    public string FolderPath { get; }
    private readonly FileSystemWatcher? _watcher;

    private List<ArticleTheme> _themes = new();
    private ArticleTheme _currentTheme;

    public IReadOnlyList<ArticleTheme> Themes => _themes;
    public IEnumerable<string> ThemeNames => _themes.Select(t => t.Name);

    public ArticleTheme CurrentTheme
    {
        get => _currentTheme;
        set
        {
            _currentTheme = value;
            AppDefaults.Shared.CurrentThemeName = value.Name;
            CurrentThemeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private ArticleThemesManager()
    {
        FolderPath = Path.Combine(AppConfig.DataDirectory, "Themes");
        Directory.CreateDirectory(FolderPath);
        _currentTheme = BuiltInDefault();
        Rescan();
        SelectByName(AppDefaults.Shared.CurrentThemeName);

        try
        {
            _watcher = new FileSystemWatcher(FolderPath) { EnableRaisingEvents = true, IncludeSubdirectories = true };
            _watcher.Changed += (_, _) => Rescan();
            _watcher.Created += (_, _) => Rescan();
            _watcher.Deleted += (_, _) => Rescan();
            _watcher.Renamed += (_, _) => Rescan();
        }
        catch { /* file watcher is best-effort */ }
    }

    public void Rescan()
    {
        var found = new List<ArticleTheme> { BuiltInDefault() };
        foreach (var dir in Directory.EnumerateDirectories(FolderPath, "*" + ArticleTheme.ThemeExtension))
        {
            var t = ArticleTheme.LoadFromFolder(dir, isAppTheme: false);
            if (t is not null) found.Add(t);
        }
        _themes = found;

        var sel = _themes.FirstOrDefault(t => string.Equals(t.Name, _currentTheme.Name, StringComparison.OrdinalIgnoreCase))
                 ?? _themes[0];
        if (sel.Name != _currentTheme.Name) CurrentTheme = sel;
    }

    public void SelectByName(string name)
    {
        var match = _themes.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        CurrentTheme = match ?? _themes[0];
    }

    /// <summary>Install a <c>.nnwtheme</c> zip file: expand into the Themes folder and rescan.</summary>
    public ArticleTheme? InstallFromZip(string zipPath)
    {
        var tmpRoot = Path.Combine(FolderPath, ".tmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpRoot);
        try
        {
            ZipFile.ExtractToDirectory(zipPath, tmpRoot);
            string? themeRoot = null;
            foreach (var dir in Directory.EnumerateDirectories(tmpRoot, "*", SearchOption.AllDirectories))
            {
                if (File.Exists(Path.Combine(dir, ArticleTheme.TemplateFilename)))
                {
                    themeRoot = dir;
                    break;
                }
            }
            if (themeRoot is null && File.Exists(Path.Combine(tmpRoot, ArticleTheme.TemplateFilename)))
                themeRoot = tmpRoot;
            if (themeRoot is null) return null;

            var info = ArticleTheme.TryReadInfo(themeRoot);
            var name = info.Name ?? Path.GetFileNameWithoutExtension(zipPath);
            var target = Path.Combine(FolderPath, name + ArticleTheme.ThemeExtension);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(themeRoot, target);

            Rescan();
            return _themes.FirstOrDefault(t => t.Name == name);
        }
        catch { return null; }
        finally
        {
            try { if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, recursive: true); } catch { }
        }
    }

    private static ArticleTheme BuiltInDefault()
    {
        var template = LoadResource("template.html") ?? "<html><body>[[[body]]]</body></html>";
        var style = (LoadResource("core.css") ?? "") + "\n" + (LoadResource("stylesheet.css") ?? "");
        return new ArticleTheme("(builtin)", isAppTheme: true)
        {
            Name = ArticleTheme.DefaultThemeName,
            ThemeIdentifier = "com.winnewswire.default",
            CreatorName = "WinNewsWire",
            CreatorHomePage = "https://github.com/Ranchero-Software/NetNewsWire",
            Version = "1",
            TemplateHtml = template,
            StylesheetCss = style,
        };
    }

    private static string? LoadResource(string name)
    {
        var asm = typeof(ArticleThemesManager).Assembly;
        var full = asm.GetName().Name + ".Resources." + name;
        using var s = asm.GetManifestResourceStream(full);
        if (s is null) return null;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
