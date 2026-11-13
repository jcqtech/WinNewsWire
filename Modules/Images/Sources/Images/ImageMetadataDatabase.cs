using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using WinNewsWire.Core;
using WinNewsWire.Database;

namespace WinNewsWire.Images;

public sealed class ImageMetadataDatabase : IDisposable
{
    private static readonly Lazy<ImageMetadataDatabase> SharedDatabase = new(() => new(Path.Combine(AppConfig.DataDirectory, "ImageMetadata.db")));
    public static ImageMetadataDatabase Shared => SharedDatabase.Value;
    public static bool IsSharedCreated => SharedDatabase.IsValueCreated;
    public static readonly TimeSpan FailureRetry = TimeSpan.FromDays(5);
    public static readonly TimeSpan FailureRetention = TimeSpan.FromDays(33);
    private readonly DatabaseQueue _queue;
    private readonly TimeProvider _clock;
    private volatile bool _disposed;
    private readonly ConcurrentDictionary<string, HomePageFaviconRecord> _favicons = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _feedIcons = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failures = new(StringComparer.Ordinal);
    public string DatabasePath { get; }

    public ImageMetadataDatabase(string databasePath, TimeProvider? clock = null)
    {
        DatabasePath = databasePath;
        _clock = clock ?? TimeProvider.System;
        if (databasePath != ":memory:" && Path.GetDirectoryName(databasePath) is { Length: > 0 } directory) Directory.CreateDirectory(directory);
        _queue = new(databasePath);
        try
        {
            _queue.Run(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE IF NOT EXISTS downloadFailure (url TEXT PRIMARY KEY NOT NULL, statusCode INTEGER, lastChecked REAL NOT NULL);
                    CREATE TABLE IF NOT EXISTS homePageFavicon (homePageURL TEXT PRIMARY KEY NOT NULL, faviconURL TEXT, lastChecked REAL NOT NULL);
                    CREATE TABLE IF NOT EXISTS feedIconURL (feedURL TEXT PRIMARY KEY NOT NULL, iconURL TEXT NOT NULL, lastChecked REAL NOT NULL);
                    CREATE TABLE IF NOT EXISTS articleThumbnailURLs (articleID TEXT PRIMARY KEY NOT NULL, thumbnailURLs TEXT, lastChecked REAL NOT NULL);
                    """;
                command.ExecuteNonQuery();
                DownloadFailureTable.RemoveExpired(connection, _clock.GetUtcNow() - FailureRetention);
                ArticleThumbnailTable.RemoveExpired(connection, _clock.GetUtcNow() - FailureRetention);
                foreach (var row in HomePageFaviconTable.FetchAll(connection)) _favicons[row.HomePageUrl] = row;
                foreach (var pair in FeedIconUrlTable.FetchAll(connection)) _feedIcons[pair.Key] = pair.Value;
                foreach (var pair in DownloadFailureTable.FetchAll(connection)) _failures[pair.Key] = pair.Value;
                connection.VacuumIfNeeded(_clock);
            });
        }
        catch { _queue.Dispose(); throw; }
    }

    public string? FaviconUrl(string homePageUrl)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _favicons.TryGetValue(homePageUrl, out var row) ? row.FaviconUrl : null;
    }
    public bool HomePageHasNoFavicon(string homePageUrl)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _favicons.TryGetValue(homePageUrl, out var row) && row.FaviconUrl is null && _clock.GetUtcNow() - row.LastChecked < FailureRetry;
    }
    public string? IconUrl(string feedUrl)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _feedIcons.TryGetValue(feedUrl, out var url) ? url : null;
    }
    public bool RecentlyFailed(string url)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _failures.TryGetValue(url, out var date) && _clock.GetUtcNow() - date < FailureRetry;
    }

    public Task SaveHomePageFaviconAsync(string homePageUrl, string? faviconUrl)
    {
        ArgumentException.ThrowIfNullOrEmpty(homePageUrl);
        return _queue.RunAsync(connection =>
        {
            var row = new HomePageFaviconRecord(homePageUrl, faviconUrl, _clock.GetUtcNow());
            HomePageFaviconTable.Save(connection, row);
            _favicons[homePageUrl] = row;
            return Task.CompletedTask;
        });
    }

    public Task SaveFeedIconUrlAsync(string feedUrl, string iconUrl)
    {
        ArgumentException.ThrowIfNullOrEmpty(feedUrl);
        ArgumentException.ThrowIfNullOrEmpty(iconUrl);
        return _queue.RunAsync(connection =>
        {
            FeedIconUrlTable.Save(connection, feedUrl, iconUrl, _clock.GetUtcNow());
            _feedIcons[feedUrl] = iconUrl;
            return Task.CompletedTask;
        });
    }

    public Task RecordFailureAsync(string url, int? statusCode)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        return _queue.RunAsync(connection =>
        {
            var now = _clock.GetUtcNow();
            DownloadFailureTable.Save(connection, url, statusCode, now);
            _failures[url] = now;
            return Task.CompletedTask;
        });
    }

    public Task ClearFailureAsync(string url)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        return _queue.RunAsync(connection =>
        {
            DownloadFailureTable.Clear(connection, url);
            _failures.TryRemove(url, out _);
            return Task.CompletedTask;
        });
    }

    public Task RemoveExpiredFailuresAsync() => _queue.RunAsync(connection =>
    {
        var cutoff = _clock.GetUtcNow() - FailureRetention;
        DownloadFailureTable.RemoveExpired(connection, cutoff);
        foreach (var pair in _failures)
            if (pair.Value < cutoff) _failures.TryRemove(pair.Key, out _);
        return Task.CompletedTask;
    });

    public Task<IReadOnlyList<ArticleThumbnailRecord>> FetchArticleThumbnailsAsync(IEnumerable<string> articleIds)
    {
        ArgumentNullException.ThrowIfNull(articleIds);
        var ids = articleIds.Distinct(StringComparer.Ordinal).ToArray();
        return _queue.RunAsync(connection => Task.FromResult(ArticleThumbnailTable.Fetch(connection, ids)));
    }

    public Task SaveArticleThumbnailsAsync(IEnumerable<ArticleThumbnailRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var snapshot = records.ToArray();
        return _queue.RunAsync(connection =>
        {
            ArticleThumbnailTable.Save(connection, snapshot, _clock.GetUtcNow());
            return Task.CompletedTask;
        });
    }

    public Task VacuumAsync(CancellationToken ct = default) => _queue.VacuumAsync(ct);
    public void Dispose() { _queue.Dispose(); _disposed = true; _favicons.Clear(); _feedIcons.Clear(); _failures.Clear(); }
}
