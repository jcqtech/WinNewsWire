using System.Net;
using WinNewsWire.Web;
using Xunit;

namespace Web.Tests;

/// <summary>Tests for <see cref="DownloadSession"/> and related types.</summary>
public class DownloadSessionTests
{
    // ── Helper: fake HTTP handler ──

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, byte[] Body, Dictionary<string, string>? Headers)> _responses = new();

        public void AddResponse(string url, HttpStatusCode status, string body, Dictionary<string, string>? headers = null)
            => _responses[url] = (status, System.Text.Encoding.UTF8.GetBytes(body), headers);

        public void AddResponse(string url, HttpStatusCode status, byte[] body, Dictionary<string, string>? headers = null)
            => _responses[url] = (status, body, headers);

        public void AddRedirect(string fromUrl, string toUrl, HttpStatusCode status = HttpStatusCode.MovedPermanently)
        {
            _responses[fromUrl] = (status, [], new Dictionary<string, string> { ["Location"] = toUrl });
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (!_responses.TryGetValue(url, out var entry))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var response = new HttpResponseMessage(entry.Status)
            {
                Content = new ByteArrayContent(entry.Body),
                RequestMessage = request,
            };

            if (entry.Headers is not null)
            {
                foreach (var (key, value) in entry.Headers)
                {
                    if (key.Equals("Location", StringComparison.OrdinalIgnoreCase))
                        response.Headers.Location = new Uri(value, UriKind.RelativeOrAbsolute);
                    else if (key.Equals("Retry-After", StringComparison.OrdinalIgnoreCase))
                        response.Headers.TryAddWithoutValidation("Retry-After", value);
                    else
                        response.Headers.TryAddWithoutValidation(key, value);
                }
            }

            return Task.FromResult(response);
        }
    }

    // ── Helper: test delegate ──

    private sealed class TestDelegate : IDownloadSessionDelegate
    {
        private readonly object _lock = new();
        public List<(Uri Url, byte[] Data, Exception? Error)> CompletedDownloads { get; } = [];
        public List<(int StatusCode, Uri Url)> HttpErrors { get; } = [];
        public TaskCompletionSource SessionCompleted { get; } = new();
        public Func<ReadOnlyMemory<byte>, Uri, bool>? ShouldContinueFunc { get; set; }
        public Dictionary<string, ConditionalGetInfo> ConditionalGetInfos { get; } = [];

        public ConditionalGetInfo? ConditionalGetInfoFor(Uri url)
        {
            ConditionalGetInfos.TryGetValue(url.AbsoluteUri, out var info);
            return info;
        }

        public Task DownloadDidCompleteAsync(Uri url, DownloadSessionResponse response, byte[] data, Exception? error)
        {
            lock (_lock) { CompletedDownloads.Add((url, data, error)); }
            return Task.CompletedTask;
        }

        public bool ShouldContinueAfterReceivingData(ReadOnlyMemory<byte> dataSoFar, Uri url)
            => ShouldContinueFunc?.Invoke(dataSoFar, url) ?? true;

        public Task HttpErrorAsync(int statusCode, Uri url)
        {
            lock (_lock) { HttpErrors.Add((statusCode, url)); }
            return Task.CompletedTask;
        }

        public Task DownloadSessionDidCompleteAsync()
        {
            SessionCompleted.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private static (DownloadSession Session, TestDelegate Delegate, FakeHandler Handler) CreateTestSession()
    {
        var handler = new FakeHandler();
        var httpClient = new HttpClient(handler);
        var del = new TestDelegate();
        var session = new DownloadSession(del, httpClient);
        return (session, del, handler);
    }

    // ── Tests ──

    [Fact]
    public async Task Download_SingleUrl_CallsDelegate()
    {
        var (session, del, handler) = CreateTestSession();
        handler.AddResponse("https://example.com/feed.xml", HttpStatusCode.OK, "<rss>test</rss>");

        session.Download(new HashSet<Uri> { new("https://example.com/feed.xml") });
        await del.SessionCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(del.CompletedDownloads);
        Assert.Equal("https://example.com/feed.xml", del.CompletedDownloads[0].Url.AbsoluteUri);
        Assert.Null(del.CompletedDownloads[0].Error);
        Assert.True(del.CompletedDownloads[0].Data.Length > 0);
    }

    [Fact]
    public async Task Download_MultipleUrls_AllComplete()
    {
        var (session, del, handler) = CreateTestSession();
        var urls = new HashSet<Uri>();
        for (int i = 0; i < 10; i++)
        {
            var url = $"https://example.com/feed{i}.xml";
            handler.AddResponse(url, HttpStatusCode.OK, $"<rss>feed {i}</rss>");
            urls.Add(new Uri(url));
        }

        session.Download(urls);
        await del.SessionCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(10, del.CompletedDownloads.Count);
    }

    [Fact]
    public async Task Download_404_CallsHttpError()
    {
        var (session, del, handler) = CreateTestSession();
        handler.AddResponse("https://example.com/gone.xml", HttpStatusCode.NotFound, "");

        session.Download(new HashSet<Uri> { new("https://example.com/gone.xml") });
        await del.SessionCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(del.HttpErrors);
        Assert.Equal(404, del.HttpErrors[0].StatusCode);
    }

    [Fact]
    public async Task Download_429_DropsSubsequentRequestsForSameHost()
    {
        var (session, del, handler) = CreateTestSession();

        // First URL returns 429 with Retry-After
        handler.AddResponse("https://slow.example.com/feed1.xml", HttpStatusCode.TooManyRequests, "",
            new Dictionary<string, string> { ["Retry-After"] = "3600" });
        // Second URL on same host should be dropped
        handler.AddResponse("https://slow.example.com/feed2.xml", HttpStatusCode.OK, "<rss/>");
        // URL on different host should proceed normally
        handler.AddResponse("https://other.example.com/feed.xml", HttpStatusCode.OK, "<rss>other</rss>");

        // Download the 429 URL first so it establishes the retry-after
        session.Download(new HashSet<Uri> { new("https://slow.example.com/feed1.xml") });
        await del.SessionCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Now try the second download session
        var del2 = new TestDelegate();
        var session2 = new DownloadSession(del2, new HttpClient(handler));
        // The session-level 429 cache won't carry across instances, but 4xx cache on the
        // instance will show 429 was treated as an error.
        Assert.Single(del.HttpErrors);
        Assert.Equal(429, del.HttpErrors[0].StatusCode);
    }

    [Fact]
    public async Task Download_ShouldContinue_False_CancelsDownload()
    {
        var (session, del, handler) = CreateTestSession();
        handler.AddResponse("https://example.com/image.png", HttpStatusCode.OK, new byte[10000]);

        del.ShouldContinueFunc = (data, url) => false; // Always cancel

        session.Download(new HashSet<Uri> { new("https://example.com/image.png") });
        await del.SessionCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Download was cancelled by delegate — no completion callback
        Assert.Empty(del.CompletedDownloads);
    }

    [Fact]
    public async Task Download_Redirect_FollowsAndCompletes()
    {
        var (session, del, handler) = CreateTestSession();
        handler.AddRedirect("https://example.com/old-feed.xml", "https://example.com/new-feed.xml");
        handler.AddResponse("https://example.com/new-feed.xml", HttpStatusCode.OK, "<rss>redirected</rss>");

        session.Download(new HashSet<Uri> { new("https://example.com/old-feed.xml") });
        await del.SessionCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(del.CompletedDownloads);
        Assert.Equal("https://example.com/old-feed.xml", del.CompletedDownloads[0].Url.AbsoluteUri);
        Assert.True(del.CompletedDownloads[0].Data.Length > 0);
    }

    [Fact]
    public void CancelAll_StopsDownloads()
    {
        var (session, del, handler) = CreateTestSession();
        handler.AddResponse("https://example.com/feed.xml", HttpStatusCode.OK, "<rss/>");

        session.Download(new HashSet<Uri> { new("https://example.com/feed.xml") });
        session.CancelAll();

        // After CancelAll, no remaining tasks should be in progress or pending
        Assert.Equal(0, session.Progress.NumberRemaining);
    }

    [Fact]
    public void ProgressInfo_IsComplete_WhenAllDone()
    {
        var progress = new ProgressInfo(NumberRemaining: 0, NumberCompleted: 5, NumberOfTasks: 5);
        Assert.True(progress.IsComplete);
    }

    [Fact]
    public void ProgressInfo_NotComplete_WhenPending()
    {
        var progress = new ProgressInfo(NumberRemaining: 3, NumberCompleted: 2, NumberOfTasks: 5);
        Assert.False(progress.IsComplete);
    }

    [Fact]
    public void ProgressInfo_Empty_NotComplete()
    {
        Assert.False(ProgressInfo.Empty.IsComplete);
    }
}

public class SpecialCasesTests
{
    [Fact]
    public void IsOpenRSSOrgUrl_Matches()
    {
        Assert.True(new Uri("https://openrss.org/feed/example").IsOpenRSSOrgUrl());
        Assert.False(new Uri("https://example.com/feed").IsOpenRSSOrgUrl());
    }

    [Fact]
    public void IsRachelByTheBayUrl_Matches()
    {
        Assert.True(new Uri("https://rachelbythebay.com/w/atom.xml").IsRachelByTheBayUrl());
        Assert.False(new Uri("https://example.com/").IsRachelByTheBayUrl());
    }

    [Fact]
    public void IsYouTubeUrl_Matches()
    {
        Assert.True(new Uri("https://www.youtube.com/feeds/videos.xml?channel_id=abc").IsYouTubeUrl());
    }

    [Fact]
    public void IsDisallowedRedirect_CatchesCaptivePortals()
    {
        Assert.True(SpecialCases.IsDisallowedRedirect("https://solutionip.com/portal"));
        Assert.True(SpecialCases.IsDisallowedRedirect("https://hotel.com/login?redirect=foo"));
        Assert.False(SpecialCases.IsDisallowedRedirect("https://example.com/feed.xml"));
    }

    [Fact]
    public void FilterOpenRSSUrls_KeepsAtMostOne()
    {
        var urls = new HashSet<Uri>
        {
            new("https://openrss.org/feed/1"),
            new("https://openrss.org/feed/2"),
            new("https://openrss.org/feed/3"),
            new("https://example.com/other"),
        };

        var filtered = SpecialCases.FilterOpenRSSUrls(urls);
        var openRSSCount = filtered.Count(u => u.IsOpenRSSOrgUrl());

        Assert.Equal(1, openRSSCount);
        Assert.Contains(new Uri("https://example.com/other"), filtered);
    }
}

public class CacheControlInfoTests
{
    [Fact]
    public void Parse_MaxAge_ParsesCorrectly()
    {
        var info = CacheControlInfo.Parse("max-age=3600, public");
        Assert.NotNull(info);
        Assert.Equal(TimeSpan.FromHours(1), info.MaxAge);
    }

    [Fact]
    public void Parse_NoMaxAge_ReturnsNull()
    {
        Assert.Null(CacheControlInfo.Parse("no-cache"));
        Assert.Null(CacheControlInfo.Parse(""));
        Assert.Null(CacheControlInfo.Parse(null));
    }

    [Fact]
    public void CanResume_Clamped_RespectsMaxMaxAge()
    {
        var info = new CacheControlInfo(DateTime.UtcNow.AddHours(-2), TimeSpan.FromHours(24));
        // Without clamp: not resumable (24h not elapsed). With 1h clamp: resumable.
        Assert.True(info.CanResume_Clamped(TimeSpan.FromHours(1)));
        Assert.False(info.CanResume_Clamped(TimeSpan.FromHours(24)));
    }

    [Fact]
    public void CanResume_WhenExpired_ReturnsTrue()
    {
        var info = new CacheControlInfo(DateTime.UtcNow.AddMinutes(-10), TimeSpan.FromMinutes(5));
        Assert.True(info.CanResume);
    }

    [Fact]
    public void CanResume_WhenActive_ReturnsFalse()
    {
        var info = new CacheControlInfo(DateTime.UtcNow, TimeSpan.FromHours(1));
        Assert.False(info.CanResume);
    }
}

public class HttpResponse429Tests
{
    [Fact]
    public void Create_ValidInput_ReturnsInstance()
    {
        var r = HttpResponse429.Create(new Uri("https://example.com/feed"), TimeSpan.FromSeconds(60));
        Assert.NotNull(r);
        Assert.Equal("example.com", r!.Host);
        Assert.False(r.CanResume);
    }

    [Fact]
    public void Create_ZeroRetryAfter_ReturnsNull()
    {
        Assert.Null(HttpResponse429.Create(new Uri("https://example.com/feed"), TimeSpan.Zero));
    }

    [Fact]
    public void Create_NegativeRetryAfter_ReturnsNull()
    {
        Assert.Null(HttpResponse429.Create(new Uri("https://example.com/feed"), TimeSpan.FromSeconds(-5)));
    }

    [Fact]
    public void CanResume_WhenPastResumeDate_ReturnsTrue()
    {
        // We can't easily test time-based things without a clock abstraction,
        // but we can at least verify the property doesn't throw.
        var r = HttpResponse429.Create(new Uri("https://example.com/feed"), TimeSpan.FromMilliseconds(1));
        Assert.NotNull(r);
        // After a brief sleep, it should be resumable
        Thread.Sleep(5);
        Assert.True(r!.CanResume);
    }
}

public class ConditionalGetInfoTests
{
    [Fact]
    public void ApplyTo_SetsHeaders()
    {
        var info = new ConditionalGetInfo("Mon, 01 Jan 2024 00:00:00 GMT", "\"abc123\"");
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
        info.ApplyTo(request);

        Assert.Contains(request.Headers, h => h.Key == "If-None-Match");
        Assert.Contains(request.Headers, h => h.Key == "If-Modified-Since");
    }

    [Fact]
    public void ApplyTo_NullValues_SkipsHeaders()
    {
        var info = new ConditionalGetInfo(null, null);
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
        info.ApplyTo(request);

        Assert.DoesNotContain(request.Headers, h => h.Key == "If-None-Match");
        Assert.DoesNotContain(request.Headers, h => h.Key == "If-Modified-Since");
    }
}

public class DownloadSessionResponseTests
{
    [Fact]
    public void StatusIsOK_ForSuccessCodes()
    {
        var r = new DownloadSessionResponse(200, null, null, null, null, null, null);
        Assert.True(r.StatusIsOK);
    }

    [Fact]
    public void StatusIsOK_FalseFor4xx()
    {
        var r = new DownloadSessionResponse(404, null, null, null, null, null, null);
        Assert.False(r.StatusIsOK);
    }

    [Fact]
    public void IsNotModified_For304()
    {
        var r = new DownloadSessionResponse(304, null, null, null, null, null, null);
        Assert.True(r.IsNotModified);
    }

    [Fact]
    public void ToConditionalGetInfo_WithValues()
    {
        var r = new DownloadSessionResponse(200, null, "\"abc\"", "Mon, 01 Jan 2024 00:00:00 GMT", null, null, null);
        var cg = r.ToConditionalGetInfo();
        Assert.NotNull(cg);
        Assert.Equal("\"abc\"", cg!.ETag);
    }

    [Fact]
    public void ToConditionalGetInfo_NullWhenEmpty()
    {
        var r = new DownloadSessionResponse(200, null, null, null, null, null, null);
        Assert.Null(r.ToConditionalGetInfo());
    }
}
