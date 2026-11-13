using System.Diagnostics;
using System.Text.Json;

namespace WinNewsWire.Core;

/// <summary>Port of <c>AppDefaults</c>. JSON-backed user preferences under %LOCALAPPDATA%\WinNewsWire\AppDefaults.json.</summary>
public sealed class AppDefaults
{
    private static readonly Lazy<AppDefaults> SharedDefaults = new(CreateSharedDefaults);
    public static AppDefaults Shared => SharedDefaults.Value;

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, JsonElement> _values;
    public event EventHandler<string>? Changed;

    public enum FontSize { Small = 0, Medium = 1, Large = 2, VeryLarge = 3 }

    public static class Key
    {
        public const string FirstRunDate = "firstRunDate";
        public const string SidebarFontSize = "sidebarFontSize";
        public const string TimelineFontSize = "timelineFontSize";
        public const string TimelineSortDirection = "timelineSortDirection";
        public const string TimelineSortKey = "timelineSortKey";
        public const string TimelineGroupByFeed = "timelineGroupByFeed";
        public const string UseColumnLayout = "useColumnLayout";
        public const string ShowStatusBar = "showStatusBar";
        public const string ShowStatusBarThemeMenu = "showStatusBarThemeMenu";
        public const string StatusBarCountMode = "statusBarCountMode";
        public const string ReadFilters = "readFilters";
        public const string SearchAllAccounts = "searchAllAccounts";
        public const string DetailFontSize = "detailFontSize";
        public const string OpenInBrowserInBackground = "openInBrowserInBackground";
        public const string SubscribeToFeedsInDefaultBrowser = "subscribeToFeedsInDefaultBrowser";
        public const string ArticleTextSize = "articleTextSize";
        public const string RefreshInterval = "refreshInterval";
        public const string RefreshIntervalVersion = "refreshIntervalVersion";
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
        public const string AutomationEnabled = "automationEnabled";
        public const string UseSoftwareRasterization = "useSoftwareRasterization";
        public const string UseDirectBrowserScrolling = "useDirectBrowserScrolling";
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

    public AppDefaults(string? path = null)
    {
        _path = path ?? Path.Combine(AppConfig.DataDirectory, "AppDefaults.json");
        _values = Load();
    }

    private static AppDefaults CreateSharedDefaults()
    {
        var defaults = new AppDefaults();
        defaults.MigrateRefreshInterval();
        return defaults;
    }

    public void MigrateRefreshInterval()
    {
        lock (_gate)
        {
            bool existed = File.Exists(_path);
            var persisted = existed
                ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(_path))
                    ?? throw new InvalidDataException("Application preferences must contain an object.")
                : new Dictionary<string, JsonElement>();
            int version = persisted.TryGetValue(Key.RefreshIntervalVersion, out var format) ? format.GetInt32() : 0;
            if (version is not (0 or 1))
                throw new InvalidDataException($"Refresh interval preference version {version} is not supported.");
            bool hasInterval = persisted.TryGetValue(Key.RefreshInterval, out var interval);
            int raw = hasInterval ? interval.GetInt32() : 3;
            // Unversioned Windows JSON used a different enum. The marker prevents remapping native values on later launches.
            int native = version == 1 ? NormalizeNativeRefreshInterval(hasInterval ? raw : 5)
                : !existed ? 5 : raw switch
                {
                    0 => 1,
                    1 or 2 => 3,
                    3 => 4,
                    4 => 5,
                    5 => 6,
                    6 => 7,
                    _ => InvalidRefreshInterval(raw)
                };
            if (version == 1 && hasInterval && raw == native)
            {
                _values = persisted;
                return;
            }

            var previous = _values;
            persisted[Key.RefreshInterval] = JsonSerializer.SerializeToElement(native);
            persisted[Key.RefreshIntervalVersion] = JsonSerializer.SerializeToElement(1);
            _values = persisted;
            try { Save(); }
            catch
            {
                _values = previous;
                throw;
            }
        }
        Changed?.Invoke(this, Key.RefreshInterval);
        Changed?.Invoke(this, Key.RefreshIntervalVersion);
    }

    private static int NormalizeNativeRefreshInterval(int value) => value switch
    {
        1 or 3 or 4 or 5 or 6 or 7 => value,
        2 => 3,
        _ => InvalidRefreshInterval(value)
    };

    private static int InvalidRefreshInterval(int value)
    {
        Trace.TraceWarning($"Unknown refresh interval value {value}; using the native two-hour default.");
        return 5;
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
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                FileUtilities.WriteAllTextAtomically(_path, JsonSerializer.Serialize(_values, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                LogFailure("Save", ex);
                throw;
            }
        }
    }

    private T? Get<T>(string key, T? fallback = default)
    {
        JsonElement el;
        lock (_gate)
            if (!_values.TryGetValue(key, out el)) return fallback;
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
        lock (_gate)
        {
            bool existed = _values.TryGetValue(key, out var previous);
            _values[key] = JsonSerializer.SerializeToElement(value);
            try { Save(); }
            catch
            {
                if (existed) _values[key] = previous;
                else _values.Remove(key);
                throw;
            }
        }
        Changed?.Invoke(this, key);
    }

    public T? GetValue<T>(string key, T? fallback = default) => Get(key, fallback);
    public void SetValue<T>(string key, T value) => Set(key, value);
    public void SetValues(IReadOnlyDictionary<string, JsonElement> values)
    {
        if (values.Count == 0) return;
        lock (_gate)
        {
            var previous = new Dictionary<string, JsonElement>(_values, StringComparer.Ordinal);
            foreach (var (key, value) in values) _values[key] = value;
            try { Save(); }
            catch { _values = previous; throw; }
        }
        foreach (var key in values.Keys) Changed?.Invoke(this, key);
    }
    public bool ContainsKey(string key) { lock (_gate) return _values.ContainsKey(key); }
    public IReadOnlyList<string> Keys { get { lock (_gate) return _values.Keys.ToList(); } }

    public void RemoveValue(string key)
    {
        lock (_gate)
        {
            if (!_values.Remove(key, out var previous)) return;
            try { Save(); }
            catch { _values[key] = previous; throw; }
        }
        Changed?.Invoke(this, key);
    }

    public int RefreshIntervalRaw
    {
        get => NormalizeNativeRefreshInterval(Get(Key.RefreshInterval, 5));
        set
        {
            if (value is not (1 or 3 or 4 or 5 or 6 or 7)) throw new ArgumentOutOfRangeException(nameof(value));
            SetValues(new Dictionary<string, JsonElement>
            {
                [Key.RefreshInterval] = JsonSerializer.SerializeToElement(value),
                [Key.RefreshIntervalVersion] = JsonSerializer.SerializeToElement(1)
            });
        }
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

    public string TimelineSortKey
    {
        get => Get(Key.TimelineSortKey, "Date") ?? "Date";
        set => Set(Key.TimelineSortKey, value);
    }

    public Dictionary<string, bool> ReadFilters
    {
        get => Get<Dictionary<string, bool>>(Key.ReadFilters) ?? new();
        set => Set(Key.ReadFilters, value);
    }

    public bool SearchAllAccounts
    {
        get => Get(Key.SearchAllAccounts, false);
        set => Set(Key.SearchAllAccounts, value);
    }

    public string? ImportOpmlAccountID
    {
        get => Get<string>(Key.ImportOpmlAccountID);
        set => Set(Key.ImportOpmlAccountID, value);
    }

    public string? ExportOpmlAccountID
    {
        get => Get<string>(Key.ExportOpmlAccountID);
        set => Set(Key.ExportOpmlAccountID, value);
    }

    public bool OpenInBrowserInBackground
    {
        get => Get(Key.OpenInBrowserInBackground, false);
        set => Set(Key.OpenInBrowserInBackground, value);
    }

    public bool UseSoftwareRasterization
    {
        get => Get(Key.UseSoftwareRasterization, false);
        set => Set(Key.UseSoftwareRasterization, value);
    }

    public bool UseDirectBrowserScrolling
    {
        get => Get(Key.UseDirectBrowserScrolling, false);
        set => Set(Key.UseDirectBrowserScrolling, value);
    }

    public string? DefaultBrowserID
    {
        get => Get<string?>(Key.DefaultBrowserID);
        set => Set(Key.DefaultBrowserID, value);
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

    public bool UseColumnLayout
    {
        get => Get(Key.UseColumnLayout, false);
        set => Set(Key.UseColumnLayout, value);
    }

    public bool ShowStatusBar
    {
        get => Get(Key.ShowStatusBar, true);
        set => Set(Key.ShowStatusBar, value);
    }

    public bool ShowStatusBarThemeMenu
    {
        get => Get(Key.ShowStatusBarThemeMenu, true);
        set => Set(Key.ShowStatusBarThemeMenu, value);
    }

    public bool ShowDebugMenu
    {
        get => Get(Key.ShowDebugMenu, false);
        set => Set(Key.ShowDebugMenu, value);
    }

    /// <summary>Global toast-notification toggle. When false, the
    /// <c>NewArticleNotifier</c> suppresses every notification regardless of
    /// the per-feed flag. Default is true so a fresh install behaves like
    /// Mac NetNewsWire (notifications opted-in per feed via context menu).</summary>
    public bool NotificationsEnabled
    {
        get => Get("notificationsEnabled", true);
        set => Set("notificationsEnabled", value);
    }

    public bool AutomationEnabled
    {
        get => Get(Key.AutomationEnabled, false);
        set => Set(Key.AutomationEnabled, value);
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
