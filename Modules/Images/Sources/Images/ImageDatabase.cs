using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using WinNewsWire.Core;
using WinNewsWire.Database;
using WinNewsWire.Parsers;

namespace WinNewsWire.Images;

public sealed class ImageDatabase : IDisposable
{
    private static readonly Lazy<ImageDatabase> SharedDatabase = new(() => new(Path.Combine(AppConfig.DataDirectory, "Images.db"),
        legacyDatabasePath: Path.Combine(AppConfig.DataDirectory, "ImageMetadata.db")));
    public static ImageDatabase Shared => SharedDatabase.Value;
    public static bool IsSharedCreated => SharedDatabase.IsValueCreated;
    public static readonly TimeSpan FailureRetry = TimeSpan.FromDays(5);
    public static readonly TimeSpan FailureRetention = TimeSpan.FromDays(33);
    private readonly DatabaseQueue _queue;
    private readonly TimeProvider _clock;
    private volatile bool _disposed;
    private readonly ConcurrentDictionary<string, HomePageFaviconRecord> _favicons = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _feedIcons = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _unsuitableThumbnails = new(StringComparer.Ordinal);
    public string DatabasePath { get; }

    public ImageDatabase(string databasePath, TimeProvider? clock = null, string? legacyDatabasePath = null)
    {
        if (legacyDatabasePath is not null && databasePath != ":memory:"
            && string.Equals(Path.GetFullPath(databasePath), Path.GetFullPath(legacyDatabasePath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The legacy image database must differ from the destination.", nameof(legacyDatabasePath));
        DatabasePath = databasePath;
        _clock = clock ?? TimeProvider.System;
        if (databasePath != ":memory:" && Path.GetDirectoryName(databasePath) is { Length: > 0 } directory) Directory.CreateDirectory(directory);
        _queue = new(databasePath, JournalMode.Wal);
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
                    CREATE TABLE IF NOT EXISTS downloadedImage (url TEXT PRIMARY KEY NOT NULL, byteCount INTEGER NOT NULL, pixelWidth INTEGER, pixelHeight INTEGER, lastAccessed REAL NOT NULL, etag TEXT, lastModified TEXT, nextCheck REAL NOT NULL DEFAULT 0);
                    """;
                command.ExecuteNonQuery();
                DownloadedImageTable.EnsureValidationColumn(connection);
                bool imported = legacyDatabasePath is not null && ImportLegacyDatabase(connection, legacyDatabasePath);
                DownloadFailureTable.RemoveExpired(connection, _clock.GetUtcNow() - FailureRetention);
                ArticleThumbnailTable.RemoveExpired(connection, _clock.GetUtcNow() - FailureRetention);
                foreach (var row in HomePageFaviconTable.FetchAll(connection)) _favicons[row.HomePageUrl] = row;
                foreach (var pair in FeedIconUrlTable.FetchAll(connection)) _feedIcons[pair.Key] = pair.Value;
                foreach (var pair in DownloadFailureTable.FetchAll(connection)) _failures[pair.Key] = pair.Value;
                foreach (var row in DownloadedImageTable.FetchPixelSizes(connection))
                    UpdateThumbnailSuitability(row.Url, row.PixelSize);
                connection.Checkpoint();
                if (imported && legacyDatabasePath is not null) DeleteLegacyDatabase(legacyDatabasePath);
            });
        }
        catch { _queue.Dispose(); throw; }
    }

    private static bool ImportLegacyDatabase(SqliteConnection connection, string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("Image database migration requires a regular legacy database file: " + path);
        connection.Execute("ATTACH DATABASE $path AS legacy;", ("$path", path));
        try
        {
            using var transaction = connection.BeginTransaction();
            (string Table, string Columns, bool Optional)[] tables = [
                ("downloadFailure", "url,statusCode,lastChecked", false),
                ("homePageFavicon", "homePageURL,faviconURL,lastChecked", false),
                ("feedIconURL", "feedURL,iconURL,lastChecked", false),
                ("articleThumbnailURLs", "articleID,thumbnailURLs,lastChecked", true),
                ("downloadedImage", "url,byteCount,pixelWidth,pixelHeight,lastAccessed,etag,lastModified,nextCheck", true)
            ];
            foreach (var (table, columns, optional) in tables)
            {
                if (optional && connection.Scalar<long>("SELECT count(*) FROM legacy.sqlite_master WHERE type='table' AND name=$table;",
                    ("$table", table)) == 0) continue;
                string selection = columns;
                if (table == "downloadedImage" && !connection.Query("PRAGMA legacy.table_info(downloadedImage);",
                    row => row.GetString(1)).Contains("nextCheck", StringComparer.OrdinalIgnoreCase))
                    selection = columns[..^"nextCheck".Length] + "0";
                int count = connection.Execute($"INSERT OR IGNORE INTO main.{table} ({columns}) SELECT {selection} FROM legacy.{table};");
                Trace.TraceInformation($"Imported {count} image records from legacy {table}.");
            }
            transaction.Commit();
        }
        finally { connection.Execute("DETACH DATABASE legacy;"); }
        return true;
    }

    private static void DeleteLegacyDatabase(string path)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(path + suffix); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Trace.TraceError($"Could not remove migrated image database {path + suffix}: {error}");
                throw;
            }
        }
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

    public bool IsUnsuitableForThumbnail(string url)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _unsuitableThumbnails.ContainsKey(url);
    }

    public Task SaveDownloadedImageAsync(DownloadedImageRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.Url);
        ArgumentOutOfRangeException.ThrowIfNegative(record.ByteCount);
        return _queue.RunAsync(connection =>
        {
            DownloadedImageTable.Save(connection, record, _clock.GetUtcNow());
            UpdateThumbnailSuitability(record.Url, record.PixelSize);
            return Task.CompletedTask;
        });
    }

    private void UpdateThumbnailSuitability(string url, (uint Width, uint Height)? pixelSize)
    {
        if (pixelSize is { } size && SignificantImageFilter.HasSignificantDimensions(size.Width, size.Height))
            _unsuitableThumbnails.TryRemove(url, out _);
        else _unsuitableThumbnails[url] = 0;
    }

    public Task SaveDownloadedImageAccessDateAsync(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        return _queue.RunAsync(connection =>
        {
            DownloadedImageTable.SaveAccessDate(connection, url, _clock.GetUtcNow());
            return Task.CompletedTask;
        });
    }

    public Task<DownloadedImageValidation?> FetchDownloadedImageValidationAsync(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        return _queue.RunAsync(connection => Task.FromResult(DownloadedImageTable.FetchValidation(connection, url)));
    }

    public Task SaveDownloadedImageNextCheckAsync(string url, DateTimeOffset nextCheck)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        return _queue.RunAsync(connection =>
        {
            DownloadedImageTable.SaveNextCheck(connection, url, nextCheck);
            return Task.CompletedTask;
        });
    }

    public Task<IReadOnlyList<DownloadedImageUsage>> FetchDownloadedImageUsageByLeastRecentAccessAsync()
        => _queue.RunAsync(connection => Task.FromResult(DownloadedImageTable.FetchUsageByLeastRecentAccess(connection)));

    public Task DeleteDownloadedImagesAsync(IEnumerable<string> urls)
    {
        ArgumentNullException.ThrowIfNull(urls);
        var snapshot = urls.Distinct(StringComparer.Ordinal).ToArray();
        return _queue.RunAsync(connection =>
        {
            if (snapshot.Length == 0) return Task.CompletedTask;
            DownloadedImageTable.Delete(connection, snapshot);
            foreach (var url in snapshot) _unsuitableThumbnails.TryRemove(url, out _);
            return Task.CompletedTask;
        });
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

    public Task VacuumAsync(CancellationToken ct = default) => _queue.CheckpointAsync(ct);
    public void Dispose()
    {
        _queue.Dispose();
        _disposed = true;
        _favicons.Clear();
        _feedIcons.Clear();
        _failures.Clear();
        _unsuitableThumbnails.Clear();
    }
}
