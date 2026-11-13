using System.Collections.Concurrent;
using System.Diagnostics;
using WinNewsWire.ActivityLog;
using WinNewsWire.Parsers;
using WinNewsWire.Web;
using ActivityLogger = WinNewsWire.ActivityLog.ActivityLog;
using ActivityKind = WinNewsWire.ActivityLog.ActivityKind;

namespace WinNewsWire.HTMLMetadata;

public sealed class HtmlMetadataDownloader : IAsyncDisposable
{
    private static readonly Lazy<HtmlMetadataDownloader> SharedDownloader = new(() => new(HtmlMetadataDatabase.Shared));
    public static HtmlMetadataDownloader Shared => SharedDownloader.Value;
    public static bool IsSharedCreated => SharedDownloader.IsValueCreated;
    public static readonly TimeSpan AttemptInterval = TimeSpan.FromHours(3);
    private readonly HtmlMetadataDatabase _database;
    private readonly Downloader _downloader;
    private readonly TimeProvider _clock;
    private readonly ActivityLogger _log;
    private readonly SynchronizationContext? _notifications;
    private readonly object _gate = new();
    private readonly Dictionary<string, Task<HtmlMetadataRecord?>> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _attemptDates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HtmlMetadataRecord> _cache = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _stopping;
    private Task? _shutdown;
    public event EventHandler<HtmlMetadataRecord>? MetadataAvailable;

    public HtmlMetadataDownloader(HtmlMetadataDatabase database, Downloader? downloader = null, TimeProvider? clock = null, ActivityLogger? activityLog = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
        _downloader = downloader ?? Downloader.Shared;
        _clock = clock ?? TimeProvider.System;
        _log = activityLog ?? ActivityLogger.Shared;
        _notifications = SynchronizationContext.Current;
    }

    public static bool ShouldSkip(string url) => SpecialCases.ContainsSpecialCase(url,
        [SpecialCases.RachelByTheBayHostName, SpecialCases.OpenRSSOrgHostName]);

    private static string HostIgnoringWww(Uri url)
        => url.IdnHost.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? url.IdnHost[4..] : url.IdnHost;

    public HtmlMetadataRecord? CachedMetadata(string url)
    {
        if (ShouldSkip(url)) return null;
        lock (_gate) ObjectDisposedException.ThrowIf(_stopping, this);
        if (_cache.TryGetValue(url, out var record)) return record;
        _ = PopulateCacheAsync(url);
        return null;
    }

    private async Task PopulateCacheAsync(string url)
    {
        try { await GetMetadataAsync(url).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or IOException or ArgumentException
            or Microsoft.Data.Sqlite.SqliteException or System.Text.Json.JsonException or InvalidOperationException)
        {
            Trace.TraceError($"Could not fetch HTML metadata for {url}: {error}");
        }
    }

    public async Task<HtmlMetadataRecord?> GetMetadataAsync(string url, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https"))
            throw new ArgumentException("HTML metadata requires an HTTP or HTTPS URL.", nameof(url));
        if (ShouldSkip(url)) return null;
        Task<HtmlMetadataRecord?> pending;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (!_pending.TryGetValue(url, out pending!))
            {
                pending = Task.Run(async () =>
                {
                    try { return await LoadAsync(url, _lifetime.Token).ConfigureAwait(false); }
                    finally { lock (_gate) _pending.Remove(url); }
                });
                _pending.Add(url, pending);
            }
        }
        return await pending.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<HtmlMetadataRecord?> LoadAsync(string url, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (await _database.CachedRecordAsync(url).ConfigureAwait(false) is { } cached)
            return Publish(cached);
        if (await _database.RecentlyFailedAsync(url).ConfigureAwait(false)) return null;
        bool retry;
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            retry = !_attemptDates.TryGetValue(url, out var previous) || now - previous >= AttemptInterval;
            if (retry) _attemptDates[url] = now;
        }
        if (!retry) return await ReturnStaleAsync(url).ConfigureAwait(false);

        var previousDownload = await _database.LastDownloadDateAsync(url).ConfigureAwait(false);
        int activity = _log.CreateActivity(ActivityOwner.HtmlMetadataDownloader, ActivityKind.DownloadHtmlMetadata(url), previousDownload?.ToString("O"));
        _log.DidStart(activity);
        DownloadResult response;
        try { response = await _downloader.DownloadAsync(url, null, ct, UserAgentStyle.Browser).ConfigureAwait(false); }
        catch (OperationCanceledException error) when (ct.IsCancellationRequested) { _log.DidFail(activity, error); throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        {
            _log.DidFail(activity, error);
            await _database.NoteTransientFailureAsync(url).ConfigureAwait(false);
            return await ReturnStaleAsync(url).ConfigureAwait(false);
        }

        var responseUrl = new Uri(response.Url);
        if (!string.Equals(HostIgnoringWww(new Uri(url)), HostIgnoringWww(responseUrl), StringComparison.OrdinalIgnoreCase))
        {
            _log.DidFail(activity, new HttpRequestException("HTML metadata redirected to a different host: " + responseUrl.IdnHost));
            return await ReturnStaleAsync(url).ConfigureAwait(false);
        }

        if (!response.Success || response.Data is not { Length: > 0 } bytes)
        {
            var error = new HttpRequestException("HTML metadata download returned HTTP " + (int)response.StatusCode
                + (response.Success ? " with no metadata bytes." : "."), null, response.StatusCode);
            _log.DidFail(activity, error);
            if ((int)response.StatusCode is >= 400 and <= 499)
                await _database.NoteFailureAsync(url, (int)response.StatusCode).ConfigureAwait(false);
            return await ReturnStaleAsync(url).ConfigureAwait(false);
        }

        HtmlMetadataRecord record;
        try
        {
            ct.ThrowIfCancellationRequested();
            var metadata = HtmlMetadataParser.Parse(new ParserData(response.Url, bytes));
            record = new HtmlMetadataRecord(url, metadata);
            await _database.SaveAsync(record, (int)response.StatusCode).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ArgumentException or Microsoft.Data.Sqlite.SqliteException
            or System.Text.Json.JsonException or InvalidOperationException or OperationCanceledException)
        {
            _log.DidFail(activity, error);
            throw;
        }
        _log.DidComplete(activity, ActivityLogger.DataSizeMessage(bytes.LongLength), returnedFromCache: response.ReturnedFromCache);
        return Publish(record);
    }

    private async Task<HtmlMetadataRecord?> ReturnStaleAsync(string url)
    {
        var record = await _database.StaleCachedRecordAsync(url).ConfigureAwait(false);
        return record is null ? null : Publish(record);
    }

    private HtmlMetadataRecord Publish(HtmlMetadataRecord record)
    {
        if (_cache.TryGetValue(record.Url, out var existing) && ReferenceEquals(existing, record)) return record;
        _cache[record.Url] = record;
        void Notify()
        {
            lock (_gate) { if (_stopping) return; }
            MetadataAvailable?.Invoke(this, record);
        }
        if (_notifications is not null && !ReferenceEquals(_notifications, SynchronizationContext.Current))
            _notifications.Post(_ => Notify(), null);
        else Notify();
        return record;
    }

    public void EmptyCache()
    {
        _cache.Clear();
        _database.EmptyCache();
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_shutdown is not null) return new(_shutdown);
            _stopping = true;
            _lifetime.Cancel();
            return new(_shutdown = DrainAsync(_pending.Values.ToArray()));
        }
    }

    private async Task DrainAsync(Task[] pending)
    {
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally { _cache.Clear(); _lifetime.Dispose(); }
    }
}
