using Xunit;

namespace WinNewsWire.Web.Tests;

public sealed class WindowsWebBrowserTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "browser-tests-" + Guid.NewGuid().ToString("N"));

    public WindowsWebBrowserTests() => Directory.CreateDirectory(_directory);

    private WindowsWebBrowser Browser(string name, string folder, string id)
    {
        var directory = Path.Combine(_directory, folder);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "browser.exe");
        File.WriteAllText(path, "test fixture, never executed");
        return new(name, path, id);
    }

    [Fact]
    public void BrowserListDeduplicatesPathsAndDisambiguatesNames()
    {
        var first = Browser("Browser", "A", "first");
        var second = Browser("Browser", "B", "second");
        var other = Browser("Another", "C", "other");
        var sorted = WindowsWebBrowser.SortBrowsers([second, first, other, first with { Name = "Duplicate registration" }]);
        Assert.Equal(3, sorted.Count);
        Assert.Same(other, sorted[0]);
        Assert.Same(first, sorted[1]);
        Assert.Same(second, sorted[2]);
        Assert.Equal(["Browser"], WindowsWebBrowser.DuplicateBrowserNames(sorted));
    }

    [Fact]
    public void SelectionSupportsPathsAndLegacyRegistrationIDs()
    {
        var browser = Browser("Selected", "app", "legacy-browser");
        var url = new Uri("https://example.test/a?q=one&two=three");
        foreach (var identifier in new[] { browser.Identifier, browser.RegistrationID })
        {
            var request = WindowsWebBrowser.CreateLaunchRequest(url, identifier, [browser], true);
            Assert.Equal(browser.ExecutablePath, request.BrowserPath);
            Assert.Same(url, request.Url);
            Assert.True(request.InBackground);
            Assert.False(request.SelectedBrowserUnavailable);
        }
    }

    [Fact]
    public void ExistingSavedExecutableStillOpensWithoutARegistration()
    {
        var browser = Browser("Unregistered", "app", "old-id");
        var request = WindowsWebBrowser.CreateLaunchRequest(new Uri("https://example.test"), browser.Identifier, [], false);
        Assert.Equal(browser.Identifier, request.BrowserPath);
        Assert.False(request.SelectedBrowserUnavailable);
    }

    [Fact]
    public void MissingSelectionIsAnExplicitSystemDefaultFallback()
    {
        var request = WindowsWebBrowser.CreateLaunchRequest(new Uri("https://example.test"),
            Path.Combine(_directory, "missing.exe"), [], false);
        Assert.Null(request.BrowserPath);
        Assert.True(request.SelectedBrowserUnavailable);
    }

    [Fact]
    public void MailUsesTheWindowsMailAssociation()
    {
        var browser = Browser("Selected", "app", "selected");
        var request = WindowsWebBrowser.CreateLaunchRequest(new Uri("mailto:reader@example.test"), browser.Identifier, [browser], false);
        Assert.Null(request.BrowserPath);
        Assert.False(request.SelectedBrowserUnavailable);
    }

    [Theory]
    [InlineData("netnewswire://theme?url=https%3A%2F%2Fexample.test%2Ftheme.zip")]
    [InlineData("feed:https://example.test/rss")]
    [InlineData("feeds://example.test/rss")]
    [InlineData("x-netnewswire-feed:https://example.test/rss")]
    public void ReaderLinksUseTheSystemAssociationEvenWithASelectedBrowser(string value)
    {
        var browser = Browser("Selected", "app", "selected");
        var url = new Uri(value);
        var request = WindowsWebBrowser.CreateLaunchRequest(url, browser.Identifier, [browser], false);
        Assert.Same(url, request.Url);
        Assert.Null(request.BrowserPath);
        Assert.False(request.SelectedBrowserUnavailable);
    }

    [Fact]
    public void MagnetUsesTheSystemAssociationEvenWithASelectedBrowser()
    {
        var browser = Browser("Selected", "app", "selected");
        var url = new Uri("magnet:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a");
        var request = WindowsWebBrowser.CreateLaunchRequest(url, browser.Identifier, [browser], false);
        Assert.Same(url, request.Url);
        Assert.Null(request.BrowserPath);
        Assert.False(request.SelectedBrowserUnavailable);
    }

    [Theory]
    [InlineData("file:///C:/Windows/notepad.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,test")]
    public void UnsafeSchemesNeverProduceLaunchRequests(string url)
        => Assert.Throws<ArgumentException>(() => WindowsWebBrowser.CreateLaunchRequest(new Uri(url), null, [], false));

    [Theory]
    [InlineData("", "\"\"")]
    [InlineData("one two", "\"one two\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("https://example.test/?q=\\", "\"https://example.test/?q=\\\\\"")]
    public void ShellArgumentsPreserveQuotesAndTrailingBackslashes(string input, string expected)
        => Assert.Equal(expected, WindowsWebBrowser.QuoteArgument(input));

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories)) File.Delete(file);
        foreach (var directory in Directory.GetDirectories(_directory)) Directory.Delete(directory);
        Directory.Delete(_directory);
    }
}
