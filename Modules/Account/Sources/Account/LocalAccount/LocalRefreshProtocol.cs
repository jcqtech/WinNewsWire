using System.Text.Json;
using System.Diagnostics;
using WinNewsWire.ActivityLog;
using WinNewsWire.Articles;
using WinNewsWire.Core;
using WinNewsWire.Web;

namespace WinNewsWire.Account;

internal static class LocalRefreshProtocol
{
    internal const int Version = 2;
    internal const int MaximumMessageCharacters = 16 * 1024 * 1024;
    internal const string OpenRssRefreshKey = "lastOpenRSSOrgFeedRefresh";

    internal static T Read<T>(string line)
    {
        if (line.Length > MaximumMessageCharacters) throw new InvalidDataException("The refresh worker message is too large.");
        return JsonSerializer.Deserialize<T>(line) ?? throw new InvalidDataException("The refresh worker returned an empty message.");
    }

    internal static string Write<T>(T message)
    {
        var line = JsonSerializer.Serialize(message);
        if (line.Length > MaximumMessageCharacters) throw new InvalidDataException("The refresh worker message is too large.");
        return line;
    }

    internal static bool IsWorkspace(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return false;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var name = Path.GetFileName(full);
        const string prefix = "WinNewsWire-Refresh-";
        return name.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParseExact(name[prefix.Length..], "N", out _)
            && string.Equals(Path.GetDirectoryName(full), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())),
                StringComparison.OrdinalIgnoreCase);
    }

    internal static void RemoveWorkspace(string path)
    {
        if (!IsWorkspace(path)) throw new InvalidDataException("Refusing to remove a directory not owned by the refresh worker.");
        try
        {
            if (!Directory.Exists(path)) return;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The refresh workspace was replaced with a filesystem link.");
            Directory.Delete(path, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Could not remove the closed refresh worker's temporary metadata: {error.Message}");
        }
    }
}

internal sealed record LocalRefreshCommand(string Command, Guid ID, LocalRefreshBatch? Batch = null);

internal sealed record LocalRefreshBatch(string AccountID, string Name, string DatabasePath, string Workspace,
    LocalFeedState[] Feeds, DateTime? LastOpenRssRefresh, bool DeleteOlderArticles = false);

internal sealed record LocalRefreshMessage(string Kind, Guid ID, int Version = LocalRefreshProtocol.Version,
    int ProcessID = 0, LocalFeedState? Feed = null, string[]? ChangedIDs = null, string[]? NewIDs = null,
    LocalFeedState[]? Feeds = null, WorkerActivity? Activity = null, ProgressInfo? Progress = null,
    DateTime? LastOpenRssRefresh = null, string? Error = null, LocalRefreshStatistics? Statistics = null,
    string[]? DeletedIDs = null);

internal sealed record WorkerActivity(int ID, string Url, string? Detail, ActivityState State, bool Started,
    string? Message, string? Error, bool ReturnedFromCache, bool DurationIsSignificant);

internal sealed record LocalFeedState(string FeedID, string Url, string? Name, string? EditedName,
    string? HomePageUrl, string? IconUrl, string? FaviconUrl, string? AuthorsJson,
    ConditionalGetInfo? ConditionalGet, DateTime? ConditionalGetDate, string? ContentHash,
    DateTime? LastCheck, int? ResponseCode, CacheControlInfo? CacheControl)
{
    internal static LocalFeedState From(Feed feed) => new(feed.FeedID, feed.Url, feed.Name, feed.EditedName,
        feed.HomePageUrl, feed.IconUrl, feed.FaviconUrl, AuthorsFor(feed), feed.Settings.ConditionalGetInfo,
        feed.ConditionalGetInfoDate, feed.ContentHash, feed.LastCheckDate, feed.LastResponseCode, feed.CacheControlInfo);

    internal Feed Create(string accountID)
    {
        var feed = new Feed(accountID, FeedID, Url)
        {
            Name = Name, EditedName = EditedName, HomePageUrl = HomePageUrl, IconUrl = IconUrl,
            FaviconUrl = FaviconUrl, Authors = DecodeAuthors(), ContentHash = ContentHash,
            LastCheckDate = LastCheck, LastResponseCode = ResponseCode, CacheControlInfo = CacheControl
        };
        feed.Settings.ConditionalGetInfo = ConditionalGet;
        feed.ConditionalGetInfoDate = ConditionalGetDate;
        return feed;
    }

    internal void ApplyTo(Feed feed, LocalFeedState before)
    {
        if (feed.FeedID != FeedID || feed.Url != Url)
            throw new InvalidDataException("The refresh worker changed a subscription identity.");
        while (true)
        {
            var current = feed.Settings.CurrentValues;
            var row = current;
            if (HomePageUrl != before.HomePageUrl && current.HomePageUrl == before.HomePageUrl)
                row = row with { HomePageUrl = string.IsNullOrEmpty(HomePageUrl) ? null : HomePageUrl.NormalizedUrl() };
            if (IconUrl != before.IconUrl && current.IconUrl == before.IconUrl) row = row with { IconUrl = IconUrl };
            if (FaviconUrl != before.FaviconUrl && current.FaviconUrl == before.FaviconUrl) row = row with { FaviconUrl = FaviconUrl };
            if (AuthorsJson != before.AuthorsJson
                && (current.Authors is null ? null : Author.ToJson(current.Authors)) == before.AuthorsJson)
                row = row with { Authors = DecodeAuthors() };
            if (ConditionalGet != before.ConditionalGet && current.ConditionalGetInfo == before.ConditionalGet)
                row = row with { ConditionalGetInfo = ConditionalGet };
            if (row.ConditionalGetInfo == ConditionalGet) row = row with { ConditionalGetInfoDate = ConditionalGetDate };
            if (ContentHash != before.ContentHash && current.ContentHash == before.ContentHash) row = row with { ContentHash = ContentHash };
            if (LastCheck != before.LastCheck && current.LastCheckDate == before.LastCheck) row = row with { LastCheckDate = LastCheck };
            if (ResponseCode != before.ResponseCode && current.LastResponseCode == before.ResponseCode) row = row with { LastResponseCode = ResponseCode };
            if (CacheControl != before.CacheControl && current.CacheControlInfo == before.CacheControl) row = row with { CacheControlInfo = CacheControl };
            if (feed.Settings.TryApplyRefreshMetadata(current, row)) break;
        }
        if (Name != before.Name && feed.Name == before.Name)
        {
            feed.Name = Name;
            feed.OnDisplayNameChanged();
        }
    }

    private static string? AuthorsFor(Feed feed) => feed.Authors is null ? null : Author.ToJson(feed.Authors);
    private IReadOnlySet<Author>? DecodeAuthors() => AuthorsJson is null ? null
        : Author.FromJson(AuthorsJson) ?? throw new InvalidDataException("The refresh worker returned invalid feed authors.");
}
