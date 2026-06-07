using System.Diagnostics;
using System.Text.Json;

namespace WinNewsWire.Core;

/// <summary>Port of <c>AppDefaults</c>. JSON-backed user preferences under %LOCALAPPDATA%\WinNewsWire\AppDefaults.json.</summary>
public sealed class AppDefaults
{
    public static AppDefaults Shared { get; } = new();

    private readonly string _path;
    private Dictionary<string, JsonElement> _values;
    public event EventHandler<string>? Changed;

    public enum FontSize { Small = 0, Medium = 1, Large = 2, VeryLarge = 3 }

    public static class Key
    {
        public const string FirstRunDate = "firstRunDate";
        public const string SidebarFontSize = "sidebarFontSize";
        public const string TimelineFontSize = "timelineFontSize";
        public const string TimelineSortDirection = "timelineSortDirection";
        public const string TimelineGroupByFeed = "timelineGroupByFeed";
        public const string DetailFontSize = "detailFontSize";
        public const string OpenInBrowserInBackground = "openInBrowserInBackground";
        public const string SubscribeToFeedsInDefaultBrowser = "subscribeToFeedsInDefaultBrowser";
        public const string ArticleTextSize = "articleTextSize";
        public const string RefreshInterval = "refreshInterval";
        public const string AddFeedAccountID = "addFeedAccountID";
        public const string AddFeedFolderName = "addFeedFolderName";
        public const string AddFolderAccountID = "addFolderAccountID";
        public const string ImportOpmlAccountID = "importOPMLAccountID";
        public const string ExportOpmlAccountID = "exportOPMLAccountID";
        public const string DefaultBrowserID = "defaultBrowserID";
        public const string CurrentThemeName = "currentThemeName";
        public const string ArticleContentJavascriptEnabled = "articleContentJavascriptEnabled";
        public const string ShowDebugMenu = "ShowDebugMenu";
        public const string AppearanceMode = "appearanceMode";
    }

    /// <summary>App-wide UI theme — applied to the WinUI element root and also
    /// surfaced to the article WebView so its <c>prefers-color-scheme</c> media
    /// query follows the user's choice rather than the OS setting. Mirrors the
    /// "appearance" preference Mac NNW exposes via System Settings.</summary>
    public enum Appearance
    {
        System = 0,
        Light = 1,
        Dark = 2,
    }

    private AppDefaults()
    {
        _path = Path.Combine(AppConfig.DataDirectory, "AppDefaults.json");
        _values = Load();
    }

    private Dictionary<string, JsonElement> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(_path))
                       ?? new();
            }
        }
        catch (JsonException ex)
        {
            LogFailure("Load", ex);
            QuarantineCorruptFile();
        }
        catch (IOException ex)
        {
            LogFailure("Load", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogFailure("Load", ex);
        }
        return new();
    }

    private void QuarantineCorruptFile()
    {
        try
        {
            if (File.Exists(_path))
            {
                var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                var dst = $"{_path}.corrupt-{stamp}";
                File.Move(_path, dst, overwrite: false);
            }
        }
        catch (IOException)
        {
            // Secondary failure during quarantine; original JsonException already logged.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_values, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (JsonException ex)
        {
            LogFailure("Save", ex);
        }
        catch (IOException ex)
        {
            LogFailure("Save", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogFailure("Save", ex);
        }
    }

    private T? Get<T>(string key, T? fallback = default)
    {
        if (!_values.TryGetValue(key, out var el)) return fallback;
        try
        {
            return el.Deserialize<T>();
        }
        catch (JsonException ex)
        {
            LogFailure($"Get<{typeof(T).Name}>({key})", ex);
            return fallback;
        }
    }

    private static void LogFailure(string op, Exception ex)
    {
        Debug.WriteLine($"AppDefaults: {op} failed: {ex}");
    }

    private void Set<T>(string key, T value)
    {
        _values[key] = JsonSerializer.SerializeToElement(value);
        Save();
        Changed?.Invoke(this, key);
    }

    public int RefreshIntervalRaw
    {
        get => Get(Key.RefreshInterval, 3);
        set => Set(Key.RefreshInterval, value);
    }

    public int ArticleTextSizeRaw
    {
        get => Get(Key.ArticleTextSize, (int)FontSize.Medium);
        set => Set(Key.ArticleTextSize, value);
    }

    public bool TimelineGroupByFeed
    {
        get => Get(Key.TimelineGroupByFeed, false);
        set => Set(Key.TimelineGroupByFeed, value);
    }

    public string TimelineSortDirection
    {
        get => Get(Key.TimelineSortDirection, "descending") ?? "descending";
        set => Set(Key.TimelineSortDirection, value);
    }

    public bool OpenInBrowserInBackground
    {
        get => Get(Key.OpenInBrowserInBackground, false);
        set => Set(Key.OpenInBrowserInBackground, value);
    }

    public string CurrentThemeName
    {
        get => Get(Key.CurrentThemeName, "Default") ?? "Default";
        set => Set(Key.CurrentThemeName, value);
    }

    public bool ArticleContentJavascriptEnabled
    {
        get => Get(Key.ArticleContentJavascriptEnabled, true);
        set => Set(Key.ArticleContentJavascriptEnabled, value);
    }

    public bool ReaderModeDefault
    {
        get => Get("readerModeDefault", false);
        set => Set("readerModeDefault", value);
    }

    public bool UnifiedLayout
    {
        get => Get("unifiedLayout", false);
        set => Set("unifiedLayout", value);
    }

    public bool ShowDebugMenu
    {
        get => Get(Key.ShowDebugMenu, false);
        set => Set(Key.ShowDebugMenu, value);
    }

    public DateTime? FirstRunDate
    {
        get => Get<DateTime?>(Key.FirstRunDate);
        set { if (value is not null) Set(Key.FirstRunDate, value.Value); }
    }

    /// <summary>App-wide light/dark/system appearance preference. Default is
    /// <see cref="Appearance.System"/> — follow Windows.</summary>
    public Appearance AppearanceMode
    {
        get => (Appearance)Get(Key.AppearanceMode, (int)Appearance.System);
        set => Set(Key.AppearanceMode, (int)value);
    }
}
