using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using WinNewsWire.ActivityLog;
using ActivityLogger = WinNewsWire.ActivityLog.ActivityLog;
using Xunit;

namespace WinNewsWire.FeedFinder.Tests;

public sealed class FeedFinderDiscoveryTests
{
    private const string Rss = """<rss version="2.0"><channel><title>Feed</title><link>https://example.com/</link><description>Feed description</description></channel></rss>""";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RedirectedPagesAndDirectFeedsUseTheResponseUrl(bool direct)
    {
        await using var site = new Site((uri, _) => Task.FromResult(uri.AbsolutePath.EndsWith("/start")
            ? new Reply("", 302, "landing/")
            : new Reply(direct ? Rss : """<html><head><link rel="alternate" type="application/rss+xml" href="feed.xml"></head><body>Site</body></html>""")));
        var requested = site.Url + "start";
        var found = Assert.Single(await FeedFinder.FindAsync(requested, site.Token));
        Assert.Equal(site.Url + (direct ? "landing/" : "landing/feed.xml"), found.UrlString);
        Assert.Equal(2, site.Requests.Count);
        var activity = Assert.Single(ActivityLogger.Shared.CompletedActivitiesFor(ActivityOwner.FeedFinder),
            activity => activity.Kind == ActivityKind.FindFeed(requested));
        Assert.Equal(direct ? "Direct feed" : "1 feed via HTML <head>", activity.CompletionMessage);
    }

    [Fact]
    public async Task FallbackCandidatesAppendToTheSitesPathAndPreserveItsQuery()
    {
        await using var site = new Site((uri, _) => Task.FromResult(new Reply(
            uri.AbsolutePath.EndsWith("/blog") ? "<html><head></head><body>Blog</body></html>" : Rss)));
        var found = await FeedFinder.FindAsync(site.Url + "blog?view=feed", site.Token);
        Assert.Equal(new HashSet<string> { site.Url + "blog/feed/?view=feed", site.Url + "blog/index.xml?view=feed" },
            found.Select(feed => feed.UrlString).ToHashSet());
        Assert.Equal(3, site.Requests.Count);
        Assert.All(site.Requests, uri => Assert.Equal("?view=feed", uri.Query));
    }

    [Fact]
    public async Task NoValidCandidateReturnsTheNativeEmptySetAndCompletionMessage()
    {
        await using var site = new Site((uri, _) => Task.FromResult(uri.AbsolutePath.EndsWith("/blog")
            ? new Reply("<html><head></head><body>Blog</body></html>") : new Reply("missing", 404)));
        var requested = site.Url + "blog";
        Assert.Empty(await FeedFinder.FindAsync(requested, site.Token));
        var activity = Assert.Single(ActivityLogger.Shared.CompletedActivitiesFor(ActivityOwner.FeedFinder),
            activity => activity.Kind == ActivityKind.FindFeed(requested));
        Assert.Equal("No feeds found in candidate URLs", activity.CompletionMessage);
        Assert.Equal(ActivityState.Completed, activity.State);
    }

    [Fact]
    public async Task CancellationWhileCheckingACandidateIsNotReportedAsFeedNotFound()
    {
        var candidateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var site = new Site(async (uri, ct) =>
        {
            if (uri.AbsolutePath.EndsWith("/blog"))
                return new Reply("""<html><body><a href="slow-feed.xml">RSS</a></body></html>""");
            candidateStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new Reply(Rss);
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var discovery = FeedFinder.FindAsync(site.Url + "blog", cancellation.Token);
        await candidateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discovery);
    }

    private sealed record Reply(string Body, int Status = 200, string? Location = null);
    private sealed class Site : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(15));
        private readonly Task _serving;
        public ConcurrentQueue<Uri> Requests { get; } = [];
        public string Url { get; }
        public CancellationToken Token => _cancellation.Token;

        public Site(Func<Uri, CancellationToken, Task<Reply>> respond)
        {
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/{Guid.NewGuid():N}/";
            _serving = ServeAsync(respond);
        }

        private async Task ServeAsync(Func<Uri, CancellationToken, Task<Reply>> respond)
        {
            while (!Token.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(Token);
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var request = await reader.ReadLineAsync(Token) ?? throw new IOException("The fixture request was empty.");
                while (await reader.ReadLineAsync(Token) is { Length: > 0 }) { }
                var uri = new Uri(new Uri(Url), request.Split(' ')[1]);
                Requests.Enqueue(uri);
                var reply = await respond(uri, Token);
                var body = Encoding.UTF8.GetBytes(reply.Body);
                var location = reply.Location is null ? "" : $"Location: {reply.Location}\r\n";
                var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {reply.Status} Test\r\nContent-Type: text/html\r\n{location}Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headers, Token);
                await stream.WriteAsync(body, Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cancellation.Cancel();
            _listener.Stop();
            try { await _serving; }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
            catch (SocketException) when (_cancellation.IsCancellationRequested) { }
            _cancellation.Dispose();
        }
    }
}
