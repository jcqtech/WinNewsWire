using System.Diagnostics;
using WinNewsWire.Core;

namespace WinNewsWire.AppShared.ArticleThemes;

/// <summary>Port of Swift <c>ArticleThemesManager</c>. Scans <c>%LOCALAPPDATA%/WinNewsWire/Themes</c>
/// for <c>.nnwtheme</c> folders (and zips, which get expanded on import), exposes the currently
/// selected theme and raises <see cref="CurrentThemeChanged"/> when it rolls over.</summary>
public sealed class ArticleThemesManager : IDisposable
{
    private static readonly Lazy<ArticleThemesManager> SharedManager = new(() => new());
    public static ArticleThemesManager Shared => SharedManager.Value;

    public event EventHandler? CurrentThemeChanged;
    public event EventHandler? ThemeNamesChanged;

    public string FolderPath { get; }
    private readonly FileSystemWatcher? _watcher;
    private readonly AppDefaults _defaults;
    private readonly object _gate = new();
    private bool _disposed;

    private List<ArticleTheme> _themes = new();
    private ArticleTheme _currentTheme;

    public IReadOnlyList<ArticleTheme> Themes { get { lock (_gate) return _themes.ToArray(); } }
    public IEnumerable<string> ThemeNames => Themes.Select(t => t.Name);

    public ArticleTheme CurrentTheme
    {
        get { lock (_gate) return _currentTheme; }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            bool changed;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var previous = _currentTheme;
                changed = !previous.HasSameContent(value);
                _currentTheme = value;
                try
                {
                    if (_defaults.CurrentThemeName != value.Name) _defaults.CurrentThemeName = value.Name;
                }
                catch { _currentTheme = previous; throw; }
            }
            if (changed) CurrentThemeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public ArticleThemesManager(string? folderPath = null, AppDefaults? defaults = null)
    {
        FolderPath = Path.GetFullPath(folderPath ?? Path.Combine(AppConfig.DataDirectory, "Themes"));
        _defaults = defaults ?? AppDefaults.Shared;
        Directory.CreateDirectory(FolderPath);
        _currentTheme = BuiltInDefault();
        Rescan();
        SelectByName(_defaults.CurrentThemeName);
        _defaults.Changed += DefaultsChanged;

        try
        {
            _watcher = new FileSystemWatcher(FolderPath) { EnableRaisingEvents = true, IncludeSubdirectories = true };
            _watcher.Changed += OnThemeFilesChanged;
            _watcher.Created += OnThemeFilesChanged;
            _watcher.Deleted += OnThemeFilesChanged;
            _watcher.Renamed += OnThemeFilesChanged;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Trace.TraceError($"Article theme watching could not start: {ex.Message}");
        }
    }

    public void Rescan()
    {
        bool changed;
        bool namesChanged;
        lock (_gate)
        {
            if (_disposed) return;
            var found = new List<ArticleTheme> { BuiltInDefault() };
            var bundledThemes = Path.Combine(AppContext.BaseDirectory, "Themes");
            if (Directory.Exists(bundledThemes))
            {
                foreach (var dir in Directory.EnumerateDirectories(bundledThemes, "*" + ArticleTheme.ThemeExtension)
                    .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase))
                {
                    var theme = ArticleTheme.LoadFromFolder(dir, isAppTheme: true);
                    if (theme is not null) found.Add(theme);
                }
            }
            foreach (var dir in Directory.EnumerateDirectories(FolderPath, "*" + ArticleTheme.ThemeExtension))
            {
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
                var theme = ArticleTheme.LoadFromFolder(dir, isAppTheme: false);
                if (theme is not null) found.Add(theme);
            }
            found = found.DistinctBy(theme => theme.Name, StringComparer.OrdinalIgnoreCase)
                .OrderBy(theme => !theme.IsBuiltInDefault)
                .ThenBy(theme => theme.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            namesChanged = !_themes.Select(theme => theme.Name).SequenceEqual(found.Select(theme => theme.Name));
            var selected = found.FirstOrDefault(t => string.Equals(t.Name, _currentTheme.Name, StringComparison.OrdinalIgnoreCase)) ?? found[0];
            changed = !_currentTheme.HasSameContent(selected);
            var previous = _currentTheme;
            _currentTheme = selected;
            try
            {
                if (previous.Name != selected.Name) _defaults.CurrentThemeName = selected.Name;
            }
            catch { _currentTheme = previous; throw; }
            _themes = found;
        }
        if (namesChanged) ThemeNamesChanged?.Invoke(this, EventArgs.Empty);
        if (changed) CurrentThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DefaultsChanged(object? sender, string key)
    {
        if (key != AppDefaults.Key.CurrentThemeName) return;
        lock (_gate)
        {
            if (_disposed || _currentTheme.Name == _defaults.CurrentThemeName) return;
        }
        SelectByName(_defaults.CurrentThemeName);
    }

    public void SelectByName(string name)
    {
        var themes = Themes;
        var match = themes.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        CurrentTheme = match ?? themes[0];
    }

    /// <summary>Install a <c>.nnwtheme</c> zip file: expand into the Themes folder and rescan.</summary>
    public ArticleTheme InstallFromZip(string zipPath)
    {
        using var prepared = PrepareImport(zipPath);
        return prepared.Install(overwriteExisting: true);
    }

    public ArticleThemeImport PrepareImport(string zipPath)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        var tmpRoot = Path.Combine(FolderPath, ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            var themeRoot = ArticleThemeDownloader.UnzipTheme(zipPath, tmpRoot);
            var theme = ArticleTheme.LoadFromFolder(themeRoot, isAppTheme: false)
                ?? throw new InvalidDataException("The archive does not contain a valid article theme.");
            return new(this, tmpRoot, theme, Directory.Exists(Path.Combine(FolderPath, Path.GetFileName(themeRoot))));
        }
        catch
        {
            try { if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceError($"Could not clean up theme extraction directory {tmpRoot}: {ex.Message}");
            }
            throw;
        }
    }

    internal ArticleTheme InstallPreparedTheme(ArticleThemeImport prepared, bool overwriteExisting)
    {
        if (!ReferenceEquals(prepared.Manager, this)) throw new ArgumentException("The theme belongs to another import session.", nameof(prepared));
        var target = Path.Combine(FolderPath, Path.GetFileName(prepared.Theme.Path));
        var backup = Path.Combine(prepared.TemporaryRoot, ".previous-theme");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (prepared.Installed) throw new InvalidOperationException("This theme has already been installed.");
            bool replacing = Directory.Exists(target);
            if (replacing)
            {
                if (!overwriteExisting) throw new IOException("The theme already exists and replacement was not confirmed.");
                if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Cannot replace a linked article theme directory.");
                Directory.Move(target, backup);
            }
            try { Directory.Move(prepared.Theme.Path, target); }
            catch
            {
                if (replacing) Directory.Move(backup, target);
                throw;
            }
            prepared.Installed = true;
        }
        Rescan();
        return ArticleTheme.LoadFromFolder(target, isAppTheme: false)
            ?? throw new InvalidDataException("The installed article theme could not be loaded.");
    }

    private static ArticleTheme BuiltInDefault()
    {
        var template = ArticleTheme.ReadResource("template.html");
        var style = ArticleTheme.ReadResource("core.css") + "\n" + ArticleTheme.ReadResource("stylesheet.css");
        return new ArticleTheme(ArticleTheme.BuiltInDefaultPath, isAppTheme: true)
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

    private void OnThemeFilesChanged(object sender, FileSystemEventArgs e)
    {
        try { Rescan(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceError($"Could not rescan article themes: {ex.Message}");
        }
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        _defaults.Changed -= DefaultsChanged;
        _watcher?.Dispose();
    }
}
