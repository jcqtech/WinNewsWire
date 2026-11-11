using System.Text.Json;
using WinNewsWire.Articles;
using WinNewsWire.Web;
using Column = WinNewsWire.Account.FeedSettingsDatabase.Column;

namespace WinNewsWire.Account;

public sealed class FeedSettings
{
    private readonly string _feedUrl;
    private readonly object _gate = new();
    private FeedSettingsDatabase? _database;
    private volatile FeedSettingsDatabase.Row _row;
    private string? _savedRelationships;
    private WeakReference<Feed>? _feed;
    public event EventHandler<Column>? SettingChanged;

    public FeedSettings(string feedUrl, string feedID, FeedSettingsDatabase? database = null)
        : this(feedUrl, new FeedSettingsDatabase.Row(feedID), database)
        => database?.EnsureFeedExists(feedUrl, feedID);

    public FeedSettings(string feedUrl, FeedSettingsDatabase.Row row, FeedSettingsDatabase? database)
    {
        _feedUrl = feedUrl; _row = row; _database = database;
        _savedRelationships = RelationshipsJson();
    }

    public string FeedID { get => _row.FeedID; set { lock (_gate) Update(_row with { FeedID = value }, Column.FeedID); } }
    public string? HomePageUrl { get => _row.HomePageUrl; set { lock (_gate) Update(_row with { HomePageUrl = value }, Column.HomePageUrl); } }
    public string? IconUrl { get => _row.IconUrl; set { lock (_gate) Update(_row with { IconUrl = value }, Column.IconUrl); } }
    public string? FaviconUrl { get => _row.FaviconUrl; set { lock (_gate) Update(_row with { FaviconUrl = value }, Column.FaviconUrl); } }
    public string? EditedName { get => _row.EditedName; set { lock (_gate) Update(_row with { EditedName = value }, Column.EditedName); } }
    public string? ContentHash { get => _row.ContentHash; set { lock (_gate) Update(_row with { ContentHash = value }, Column.ContentHash); } }
    public bool NewArticleNotificationsEnabled { get => _row.NewArticleNotificationsEnabled; set { lock (_gate) Update(_row with { NewArticleNotificationsEnabled = value }, Column.NewArticleNotificationsEnabled); } }
    public bool ReaderViewAlwaysEnabled { get => _row.ReaderViewAlwaysEnabled; set { lock (_gate) Update(_row with { ReaderViewAlwaysEnabled = value }, Column.ReaderViewAlwaysEnabled); } }
    public IReadOnlySet<Author>? Authors { get => _row.Authors; set { lock (_gate) Update(_row with { Authors = value }, Column.Authors); } }
    public DateTime? ConditionalGetInfoDate { get => _row.ConditionalGetInfoDate; set { lock (_gate) Update(_row with { ConditionalGetInfoDate = value }, Column.ConditionalGetInfoDate); } }
    public CacheControlInfo? CacheControlInfo { get => _row.CacheControlInfo; set { lock (_gate) Update(_row with { CacheControlInfo = value }, Column.CacheControlInfoDateCreated); } }
    public string? ExternalID { get => _row.ExternalID; set { lock (_gate) Update(_row with { ExternalID = value }, Column.ExternalID); } }
    public Dictionary<string, string>? FolderRelationship { get => _row.FolderRelationship; set { lock (_gate) Update(_row with { FolderRelationship = value }, Column.FolderRelationship); } }
    public DateTime? LastCheckDate { get => _row.LastCheckDate; set { lock (_gate) Update(_row with { LastCheckDate = value }, Column.LastCheckDate); } }
    public int? LastResponseCode { get => _row.LastResponseCode; set { lock (_gate) Update(_row with { LastResponseCode = value }, Column.LastResponseCode); } }
    public ConditionalGetInfo? ConditionalGetInfo
    {
        get => _row.ConditionalGetInfo;
        set
        {
            lock (_gate)
            {
                if (_row.ConditionalGetInfo == value) return;
                Update(_row with { ConditionalGetInfo = value, ConditionalGetInfoDate = value is null ? null : DateTime.UtcNow }, Column.ConditionalGetInfoEtag);
            }
        }
    }

    private void Update(FeedSettingsDatabase.Row row, Column column)
    {
        if (!Store(row)) return;
        SettingChanged?.Invoke(this, column);
        if (column == Column.EditedName && _feed?.TryGetTarget(out var feed) == true) feed.OnDisplayNameChanged();
    }

    internal FeedSettingsDatabase.Row CurrentValues => _row;

    internal bool TryApplyRefreshMetadata(FeedSettingsDatabase.Row expected, FeedSettingsDatabase.Row row)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_row, expected)) return false;
            var old = _row;
            if (!Store(row)) return true;
            if (old.HomePageUrl != row.HomePageUrl) SettingChanged?.Invoke(this, Column.HomePageUrl);
            if (old.IconUrl != row.IconUrl) SettingChanged?.Invoke(this, Column.IconUrl);
            if (old.FaviconUrl != row.FaviconUrl) SettingChanged?.Invoke(this, Column.FaviconUrl);
            if (old.Authors != row.Authors) SettingChanged?.Invoke(this, Column.Authors);
            if (old.ContentHash != row.ContentHash) SettingChanged?.Invoke(this, Column.ContentHash);
            if (old.ConditionalGetInfo != row.ConditionalGetInfo) SettingChanged?.Invoke(this, Column.ConditionalGetInfoEtag);
            if (old.ConditionalGetInfoDate != row.ConditionalGetInfoDate) SettingChanged?.Invoke(this, Column.ConditionalGetInfoDate);
            if (old.CacheControlInfo != row.CacheControlInfo) SettingChanged?.Invoke(this, Column.CacheControlInfoDateCreated);
            if (old.LastCheckDate != row.LastCheckDate) SettingChanged?.Invoke(this, Column.LastCheckDate);
            if (old.LastResponseCode != row.LastResponseCode) SettingChanged?.Invoke(this, Column.LastResponseCode);
            return true;
        }
    }

    private bool Store(FeedSettingsDatabase.Row row)
    {
        if (_row == row) return false;
        _database?.InsertRows([(_feedUrl, row)]);
        _row = row;
        _savedRelationships = RelationshipsJson();
        return true;
    }

    internal void Attach(Feed feed) => _feed = new(feed);
    internal void BindDatabase(FeedSettingsDatabase database)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_database, database)) return;
            database.InsertRows([(_feedUrl, _row)]);
            _database = database;
            _savedRelationships = RelationshipsJson();
        }
    }
    internal void Flush()
    {
        lock (_gate)
        {
            if (_database is null || RelationshipsJson() == _savedRelationships) return;
            _database.InsertRows([(_feedUrl, _row)]);
            _savedRelationships = RelationshipsJson();
        }
    }

    private string? RelationshipsJson() => _row.FolderRelationship is null ? null : JsonSerializer.Serialize(_row.FolderRelationship);
}
