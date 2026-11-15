using WinNewsWire.AppShared.ArticleThemes;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

public sealed class ArticleThemeResourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WinNewsWire.ThemeResources." + Guid.NewGuid().ToString("N"));

    public ArticleThemeResourceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task NestedFontResourcesRetainTheirBytesAndContentType()
    {
        Directory.CreateDirectory(Path.Combine(_root, "fonts"));
        byte[] bytes = [1, 3, 5, 7];
        await File.WriteAllBytesAsync(Path.Combine(_root, "fonts", "My Font.woff2"), bytes);
        var result = await ArticleThemeResource.ReadAsync(_root, "/fonts/My%20Font.woff2");
        Assert.Equal(bytes, result.Data);
        Assert.Equal("font/woff2", result.ContentType);
    }

    [Theory]
    [InlineData("/%2e%2e/outside.txt")]
    [InlineData("/..%5coutside.txt")]
    [InlineData("/%2foutside.txt")]
    [InlineData("/file.txt%3astream")]
    [InlineData("/CON.txt")]
    public async Task ResourcePathsCannotEscapeOrAliasWindowsFiles(string path)
        => await Assert.ThrowsAsync<InvalidDataException>(() => ArticleThemeResource.ReadAsync(_root, path));

    [Fact]
    public async Task OversizedResourcesFailBeforeAllocation()
    {
        await using (var file = File.Create(Path.Combine(_root, "large.png")))
            file.SetLength(ArticleThemeDownloader.MaximumThemeSize + 1);
        await Assert.ThrowsAsync<InvalidDataException>(() => ArticleThemeResource.ReadAsync(_root, "/large.png"));
    }

    [Fact]
    public async Task CancelledResourcesDoNotStartFileAccess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ArticleThemeResource.ReadAsync(_root, "/missing.png", cancellation.Token));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
