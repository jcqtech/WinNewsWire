using System.IO.Compression;
using System.Net;
using System.Text;
using WinNewsWire.AppShared.ArticleThemes;
using WinNewsWire.Core;
using WinNewsWire.Web;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

public sealed class ArticleThemeDownloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WinNewsWire.ThemeDownload." + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task HttpThemeInstallsAndRemovesItsTemporaryArchive()
    {
        using var manager = Manager();
        string? userAgent = null;
        using var http = new HttpClient(new Handler(request =>
        {
            userAgent = request.Headers.UserAgent.ToString();
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Archive()) };
        }));
        var theme = await ArticleThemeDownloader.InstallFromUrlAsync(Url(), manager, downloader: new Downloader(http));
        Assert.Equal("Downloaded", theme.Name);
        Assert.True(Directory.Exists(theme.Path));
        Assert.Empty(Directory.EnumerateFiles(manager.FolderPath, ".download-*"));
        Assert.Equal(UserAgent.BrowserValue, userAgent);
    }

    [Fact]
    public async Task DownloadedThemeCanBeInspectedAndCancelledWithoutInstalling()
    {
        using var manager = Manager();
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Archive()) }));
        await using (var prepared = await ArticleThemeDownloader.PrepareFromUrlAsync(Url(), manager, downloader: new Downloader(http)))
        {
            Assert.Equal("Author", prepared.Theme.CreatorName);
            Assert.All(manager.Themes, theme => Assert.True(theme.IsAppTheme));
            Assert.False(Directory.Exists(Path.Combine(manager.FolderPath, "Downloaded.nnwtheme")));
        }
        Assert.Empty(Directory.EnumerateDirectories(manager.FolderPath, ".tmp-*"));
        Assert.Empty(Directory.EnumerateFiles(manager.FolderPath, ".download-*"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task DownloadLimitIsEnforcedAtTheExactBoundary(int excess)
    {
        using var manager = Manager();
        var overhead = Archive().Length;
        var archive = Archive(checked((int)ArticleThemeDownloader.MaximumThemeSize - overhead + excess));
        Assert.Equal(ArticleThemeDownloader.MaximumThemeSize + excess, archive.Length);
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) }));
        var operation = ArticleThemeDownloader.InstallFromUrlAsync(Url(), manager, downloader: new Downloader(http));
        if (excess == 0) Assert.Equal("Downloaded", (await operation).Name);
        else Assert.Contains("10 MB", (await Assert.ThrowsAsync<InvalidDataException>(() => operation)).Message);
    }

    [Fact]
    public async Task UnsupportedSchemesAreRejectedBeforeAnyRequest()
    {
        using var manager = Manager();
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException("No request should be sent.")));
        await Assert.ThrowsAsync<ArgumentException>(() => ArticleThemeDownloader.InstallFromUrlAsync(
            new("file:///C:/theme.zip"), manager, downloader: new Downloader(http)));
    }

    [Fact]
    public async Task HttpFailuresDoNotInstallAnErrorPageAsATheme()
    {
        using var manager = Manager();
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.NotFound) { Content = new StringContent("Not found") }));
        await Assert.ThrowsAsync<InvalidDataException>(() => ArticleThemeDownloader.InstallFromUrlAsync(Url(), manager, downloader: new Downloader(http)));
        Assert.All(manager.Themes, theme => Assert.True(theme.IsAppTheme));
        Assert.Empty(Directory.EnumerateFiles(manager.FolderPath));
    }

    [Fact]
    public async Task CancellationBeforeDownloadDoesNotInstallAnything()
    {
        using var manager = Manager();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ArticleThemeDownloader.InstallFromUrlAsync(Url(), manager, cancellation.Token));
        Assert.All(manager.Themes, theme => Assert.True(theme.IsAppTheme));
    }

    private ArticleThemesManager Manager()
    {
        Directory.CreateDirectory(_root);
        return new(Path.Combine(_root, "Themes"), new AppDefaults(Path.Combine(_root, "defaults.json")));
    }
    private static Uri Url() => new($"https://example.com/{Guid.NewGuid():N}/theme.zip");
    private static byte[] Archive(int padding = 0)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("Downloaded.nnwtheme/Info.json", CompressionLevel.NoCompression).Open(), new UTF8Encoding(false)))
                writer.Write("""{"name":"Downloaded","creatorName":"Author","version":1}""");
            using var content = zip.CreateEntry("Downloaded.nnwtheme/padding.bin", CompressionLevel.NoCompression).Open();
            content.Write(new byte[padding]);
        }
        return stream.ToArray();
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
    public void Dispose() => Directory.Delete(_root, recursive: true);
}
