using System.Collections.Concurrent;
using WinNewsWire.Core;
using WinNewsWire.Database;

namespace WinNewsWire.HTMLMetadata;

public sealed class HtmlMetadataDatabase : IDisposable
{
    private static readonly Lazy<HtmlMetadataDatabase> SharedDatabase = new(() => new(Path.Combine(AppConfig.DataDirectory, "HTMLMetadata.db")));
    public static HtmlMetadataDatabase Shared => SharedDatabase.Value;
    public static bool IsSharedCreated => SharedDatabase.IsValueCreated;
    public static readonly TimeSpan CacheExpiration = TimeSpan.FromHours(149);
    public static readonly TimeSpan PersistentFailureRetry = TimeSpan.FromDays(11);
    public static readonly TimeSpan TransientFailureRetry = TimeSpan.FromHours(5);
    public static readonly TimeSpan MaximumAge = TimeSpan.FromDays(30);
    public string DatabasePath { get; }
    private readonly DatabaseQueue _queue;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, HtmlMetadataTable.Entry> _cache = new(StringComparer.Ordinal);

    public HtmlMetadataDatabase(string databasePath, TimeProvider? clock = null)
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
                command.CommandText = HtmlMetadataTable.CreateSql;
                command.ExecuteNonQuery();
                HtmlMetadataTable.RemoveExpired(connection, _clock.GetUtcNow() - MaximumAge);
                connection.VacuumIfNeeded(_clock);
            });
        }
        catch { _queue.Dispose(); throw; }
    }

    public Task<HtmlMetadataRecord?> CachedRecordAsync(string url)
        => _queue.RunAsync(connection =>
        {
            var entry = CachedOrFetched(connection, url);
            return Task.FromResult(entry is { IsFailure: false } && _clock.GetUtcNow() - entry.LastChecked < CacheExpiration ? entry.Record : null);
        });

    public Task<HtmlMetadataRecord?> StaleCachedRecordAsync(string url)
        => _queue.RunAsync(connection =>
        {
            var entry = CachedOrFetched(connection, url);
            return Task.FromResult(entry is { IsFailure: false } ? entry.Record : null);
        });

    public Task<DateTimeOffset?> LastDownloadDateAsync(string url)
        => _queue.RunAsync(connection =>
        {
            var entry = CachedOrFetched(connection, url);
            return Task.FromResult(entry is { IsFailure: false } ? (DateTimeOffset?)entry.LastChecked : null);
        });

    public Task<bool> RecentlyFailedAsync(string url)
        => _queue.RunAsync(connection =>
        {
            var entry = CachedOrFetched(connection, url);
            bool failed = entry is { IsFailure: true } && _clock.GetUtcNow() - entry.LastChecked
                < (entry.StatusCode == 0 ? TransientFailureRetry : PersistentFailureRetry);
            return Task.FromResult(failed);
        });

    public Task SaveAsync(HtmlMetadataRecord record, int statusCode = 200)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (statusCode is < 200 or >= 300) throw new ArgumentOutOfRangeException(nameof(statusCode));
        return _queue.RunAsync(connection =>
        {
            var now = _clock.GetUtcNow();
            HtmlMetadataTable.Save(connection, record, statusCode, now);
            _cache[record.Url] = new(record, now, statusCode);
            return Task.CompletedTask;
        });
    }

    public Task NoteFailureAsync(string url, int statusCode)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        if (statusCode != 0 && statusCode is not (>= 400 and <= 499)) throw new ArgumentOutOfRangeException(nameof(statusCode));
        return _queue.RunAsync(connection =>
        {
            HtmlMetadataTable.NoteFailure(connection, url, statusCode, _clock.GetUtcNow());
            _cache.TryRemove(url, out _);
            return Task.CompletedTask;
        });
    }

    public Task NoteTransientFailureAsync(string url) => NoteFailureAsync(url, 0);
    public void EmptyCache() => _cache.Clear();
    public Task VacuumAsync(CancellationToken ct = default) => _queue.VacuumAsync(ct);

    public Task RemoveExpiredEntriesAsync() => _queue.RunAsync(connection =>
    {
        HtmlMetadataTable.RemoveExpired(connection, _clock.GetUtcNow() - MaximumAge);
        _cache.Clear();
        return Task.CompletedTask;
    });

    private HtmlMetadataTable.Entry? CachedOrFetched(Microsoft.Data.Sqlite.SqliteConnection connection, string url)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        if (_cache.TryGetValue(url, out var entry)) return entry;
        entry = HtmlMetadataTable.Fetch(connection, url);
        if (entry is not null) _cache[url] = entry;
        return entry;
    }

    public void Dispose() { _queue.Dispose(); _cache.Clear(); }
}
