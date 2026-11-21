using WinNewsWire.Core;
using WinNewsWire.Web;
using System.Text.Json;

namespace WinNewsWire.Account;

public sealed class AccountSettings
{
    private readonly string _accountID;
    private readonly string _dataFolder;
    private readonly AppDefaults _defaults;

    public AccountSettings(string accountID, string dataFolder)
    {
        _accountID = accountID;
        _dataFolder = dataFolder;
        Directory.CreateDirectory(dataFolder);
        _defaults = new AppDefaults(Path.Combine(dataFolder, "AccountPreferences.json"));
        if (!Get("imported", false))
        {
            if (AccountSettingsImporter.ReadSettingsFromPlist(accountID, dataFolder) is { } imported) StoreImportedSettings(imported);
            Set("imported", true);
        }
        FetchFromSettingsFileIfNeeded();
        if (Username is null || EndpointUrl is null)
        {
            var database = new AccountSettingsDatabase(Path.Combine(AppConfig.DataDirectory, "AccountSettings.db"));
            Username ??= database.Username(accountID);
            if (EndpointUrl is null && Uri.TryCreate(database.EndpointUrl(accountID), UriKind.RelativeOrAbsolute, out var url))
                EndpointUrl = url;
        }
    }

    public string? Name { get => Get<string?>("name"); set => Set("name", value); }
    public bool IsActive { get => Get("isActive", true); set => Set("isActive", value); }
    public string? Username
    {
        get => Trimmed(Get<string?>("username"));
        set { if (Trimmed(value) is { } text) Set("username", text); }
    }
    public DateTime? LastArticleFetchStartTime { get => Get<DateTime?>("lastArticleFetchStartTime"); set => Set("lastArticleFetchStartTime", value); }
    public DateTime? LastRefreshCompletedDate { get => Get<DateTime?>("lastRefreshCompletedDate"); set => Set("lastRefreshCompletedDate", value); }
    public string? ExternalID { get => Get<string?>("externalID"); set => Set("externalID", value); }
    internal bool HasOneDriveUnreadContentPreference => _defaults.ContainsKey(Key("oneDriveSyncUnreadContent"));
    public bool OneDriveSyncUnreadContent { get => Get("oneDriveSyncUnreadContent", false); set => Set("oneDriveSyncUnreadContent", value); }
    internal DateTime? OneDriveLastContentCleanupUtc { get => Get<DateTime?>("oneDriveLastContentCleanupUtc"); set => Set("oneDriveLastContentCleanupUtc", value); }
    internal IReadOnlyList<string> PendingArticleRefreshIDs
    {
        get => Get<string[]?>("pendingArticleRefreshIDs") ?? [];
        set => Set("pendingArticleRefreshIDs", value);
    }
    public Uri? EndpointUrl
    {
        get => Uri.TryCreate(Trimmed(Get<string?>("endpointURL")), UriKind.RelativeOrAbsolute, out var uri) ? uri : null;
        set { if (Trimmed(value?.ToString()) is { } text) Set("endpointURL", text); }
    }

    public ConditionalGetInfo? ConditionalGetInfo(string endpoint)
        => Get<ConditionalGetInfo?>("conditionalGetInfo-" + endpoint);

    public void SetConditionalGetInfo(string endpoint, ConditionalGetInfo? info)
        => Set("conditionalGetInfo-" + endpoint, info);

    public void FetchFromSettingsFileIfNeeded()
    {
        if (Username is not null && EndpointUrl is not null) return;
        var imported = AccountSettingsImporter.ReadSettingsFromPlist(_accountID, _dataFolder);
        if (imported is null) return;
        Username ??= imported.Username;
        EndpointUrl ??= imported.EndpointUrl;
    }

    internal void ImportWindowsMetadata(string? name, bool active, string? username, string? server)
    {
        Name ??= name;
        if (!_defaults.ContainsKey(Key("isActive"))) IsActive = active;
        Username ??= username;
        if (EndpointUrl is null && Uri.TryCreate(server, UriKind.RelativeOrAbsolute, out var uri)) EndpointUrl = uri;
    }

    internal void SetAuthenticationMetadata(string username, string? endpoint)
    {
        var values = new Dictionary<string, JsonElement>();
        if (Username != username) values[Key("username")] = JsonSerializer.SerializeToElement(username);
        if (endpoint is not null && EndpointUrl?.ToString() != endpoint)
            values[Key("endpointURL")] = JsonSerializer.SerializeToElement(endpoint);
        _defaults.SetValues(values);
    }

    public void DeleteSettings()
    {
        foreach (var key in _defaults.Keys.Where(key => key.StartsWith(_accountID + "-", StringComparison.Ordinal)).ToList())
            _defaults.RemoveValue(key);
    }

    private void StoreImportedSettings(AccountSettingsImporter.ImportedSettings imported)
    {
        Name ??= imported.Name;
        if (imported.IsActive is { } active) IsActive = active;
        Username ??= imported.Username;
        LastArticleFetchStartTime ??= imported.LastArticleFetchStartTime;
        LastRefreshCompletedDate ??= imported.LastRefreshCompletedDate;
        EndpointUrl ??= imported.EndpointUrl;
        ExternalID ??= imported.ExternalID;
        if (imported.ConditionalGetInfo is { } headers)
            foreach (var (endpoint, info) in headers)
                if (ConditionalGetInfo(endpoint) is null) SetConditionalGetInfo(endpoint, info);
    }

    private static string? Trimmed(string? text) => !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
    private string Key(string key) => _accountID + "-" + key;
    private T? Get<T>(string key, T? fallback = default) => _defaults.GetValue(Key(key), fallback);
    private void Set<T>(string key, T value) => _defaults.SetValue(Key(key), value);
}
