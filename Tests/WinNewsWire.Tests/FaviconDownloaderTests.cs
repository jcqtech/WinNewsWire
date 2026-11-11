using System.Net;
using WinNewsWire.AppShared.Favicons;
using WinNewsWire.Web;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

public sealed class FaviconDownloaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WinNewsWire.FaviconTests." + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task QueryStringsDoNotBecomeWindowsFilenames()
    {
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("<svg/>") }));
        var download = new SingleFaviconDownloader($"https://example.com/{Guid.NewGuid():N}/icon.svg?v=2", _root, new Downloader(http));
        Assert.EndsWith(".svg", download.CachePath);
        Assert.DoesNotContain("?", download.CachePath);
        var bytes = await download.LoadAsync();
        Assert.NotNull(bytes);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(download.CachePath));
        Assert.Single(Directory.EnumerateFiles(_root));
    }

    [Fact]
    public async Task HttpErrorsAreNotWrittenAsFaviconImages()
    {
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.NotFound) { Content = new StringContent("<html>Error</html>") }));
        var download = new SingleFaviconDownloader($"https://example.com/{Guid.NewGuid():N}/icon.ico", _root, new Downloader(http));
        Assert.Null(await download.LoadAsync());
        Assert.Null(download.Data);
        Assert.False(File.Exists(download.CachePath));
    }

    [Fact]
    public async Task ConcurrentIconLoadsShareOneDurableResult()
    {
        var requests = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            Interlocked.Increment(ref requests);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        }));
        var download = new SingleFaviconDownloader($"https://example.com/{Guid.NewGuid():N}/icon", _root, new Downloader(http));
        var results = await Task.WhenAll(download.LoadAsync(), download.LoadAsync());
        Assert.Equal(1, requests);
        Assert.Equal(results[0], results[1]);
        Assert.Single(Directory.EnumerateFiles(_root));
    }

    [Fact]
    public async Task CancelledLoadsDoNotWritePartialImages()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var download = new SingleFaviconDownloader("https://example.com/icon.ico", _root);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.LoadAsync(cancellation.Token));
        Assert.False(File.Exists(download.CachePath));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
