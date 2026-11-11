using Xunit;

namespace WinNewsWire.Core.Tests;

public sealed class AppActivationRequestTests
{
    [Theory]
    [InlineData("feed:https://example.test/rss", "https://example.test/rss")]
    [InlineData("feed://example.test/rss", "http://example.test/rss")]
    [InlineData("feeds://example.test/rss", "https://example.test/rss")]
    [InlineData("x-netnewswire-feed:https://example.test/rss", "https://example.test/rss")]
    [InlineData("x-winnewswire-feed://example.test/rss", "http://example.test/rss")]
    [InlineData("FEEDS://EXAMPLE.TEST/rss", "https://example.test/rss")]
    [InlineData("https://example.test/rss?a=1&b=2", "https://example.test/rss?a=1&b=2")]
    public void FeedActivationsMatchNativeNormalization(string input, string expected)
        => Assert.Equal(new AppActivationRequest(AppActivationKind.AddFeed, expected), AppActivationRequest.FromInput(input));

    [Theory]
    [InlineData("nnw://showunread", AppActivationKind.ShowUnread)]
    [InlineData("nnw://showtoday", AppActivationKind.ShowToday)]
    [InlineData("nnw://showstarred", AppActivationKind.ShowStarred)]
    [InlineData("winnewswire://showunread/", AppActivationKind.ShowUnread)]
    [InlineData("nnw://icon", AppActivationKind.ShowMainWindow)]
    public void WidgetRoutesMatchNative(string input, AppActivationKind kind)
        => Assert.Equal(kind, AppActivationRequest.FromInput(input).Kind);

    [Fact]
    public void WidgetArticleIDsDecodeOnceAndPreservePlusSigns()
    {
        var request = AppActivationRequest.FromInput("nnw://showunread?id=a%2Fb%252Fc+d&account=account%2B1");
        Assert.Equal("a/b%2Fc+d", request.ArticleID);
        Assert.Equal("account+1", request.AccountID);
    }

    [Fact]
    public void AFragmentDoesNotBecomeActivationQueryParameters()
        => Assert.Null(AppActivationRequest.FromInput("nnw://showunread#section?id=not-an-article").ArticleID);

    [Theory]
    [InlineData("netnewswire://theme/?url=https%3A%2F%2Fexample.test%2Ftheme.zip%3Fa%3D1%26b%3D2")]
    [InlineData("winnewswire://theme?url=https%3A%2F%2Fexample.test%2Ftheme.zip%3Fa%3D1%26b%3D2")]
    public void ThemeRoutesPreserveTheEmbeddedDownloadUrl(string input)
        => Assert.Equal(new(AppActivationKind.InstallThemeUrl, "https://example.test/theme.zip?a=1&b=2"), AppActivationRequest.FromInput(input));

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,example")]
    [InlineData("netnewswire://theme/?url=file%3A%2F%2FC%3A%2Fsecret.zip")]
    [InlineData("winnewswire://theme/")]
    [InlineData("winnewswire://unknown")]
    [InlineData("winnewswire://user@showunread")]
    [InlineData("winnewswire://showunread/path")]
    [InlineData("winnewswire://showunread?id=1&id=2")]
    [InlineData("winnewswire://showunread?id=%zz")]
    [InlineData("winnewswire://theme?url=%")]
    public void UnsupportedAndAmbiguousLinksAreRejected(string input)
        => Assert.Throws<ArgumentException>(() => AppActivationRequest.FromInput(input));

    [Fact]
    public void MultipleCommandLineRequestsKeepTheirOrderAndQuotedPath()
    {
        var requests = AppActivationRequest.FromArguments(
            ["--subscribe", "example.test/rss", @"C:\Feeds\My subscriptions.opml", "--install-theme", "https://example.test/theme.zip"]);
        Assert.Equal([AppActivationKind.AddFeed, AppActivationKind.ImportOpml, AppActivationKind.InstallThemeUrl], requests.Select(request => request.Kind));
        Assert.Equal(@"C:\Feeds\My subscriptions.opml", requests[1].Target);
    }

    [Fact]
    public void FileInputUsesItsExplicitWorkingDirectory()
    {
        var request = AppActivationRequest.FromInput("Feeds.OPML", @"C:\Reader");
        Assert.Equal(new(AppActivationKind.ImportOpml, @"C:\Reader\Feeds.OPML"), request);
    }

    [Fact]
    public void RedirectedRelativeFilesDoNotUseTheWrongProcessWorkingDirectory()
        => Assert.Throws<ArgumentException>(() => AppActivationRequest.FromArguments(["subscriptions.opml"]));

    [Theory]
    [InlineData("--subscribe")]
    [InlineData("--import-opml")]
    [InlineData("--install-theme")]
    [InlineData("--unknown")]
    public void InvalidCommandLineOptionsDoNotBecomeSuccessfulActivations(string option)
        => Assert.Throws<ArgumentException>(() => AppActivationRequest.FromArguments([option]));

    [Fact]
    public void EmptyLaunchShowsTheExistingMainWindow()
        => Assert.Equal(new(AppActivationKind.ShowMainWindow), Assert.Single(AppActivationRequest.FromArguments([])));

    [Theory]
    [InlineData(".opml", AppActivationKind.ImportOpml)]
    [InlineData(".XML", AppActivationKind.ImportOpml)]
    [InlineData(".nnwtheme", AppActivationKind.InstallThemeFile)]
    [InlineData(".zip", AppActivationKind.InstallThemeFile)]
    public void FileTypesDispatchToTheirImporters(string extension, AppActivationKind expected)
        => Assert.Equal(expected, AppActivationRequest.FileKind("document" + extension));

    [Fact]
    public void OptionsDoNotAcceptTheWrongFileType()
        => Assert.Throws<ArgumentException>(() => AppActivationRequest.FromArguments(["--import-opml", @"C:\Themes\theme.zip"]));

    [Theory]
    [InlineData("mailto:reader@example.test")]
    [InlineData("feed:file:///C:/private.opml")]
    [InlineData("feeds:javascript:alert(1)")]
    [InlineData("ftp://example.test/feed")]
    public void SubscriptionOptionsDoNotReinterpretOtherSchemesAsHosts(string input)
        => Assert.Throws<ArgumentException>(() => AppActivationRequest.FromArguments(["--subscribe", input]));

    [Theory]
    [InlineData("localhost:8080/feed", "http://localhost:8080/feed")]
    [InlineData("feeds://example.test:8443/feed", "https://example.test:8443/feed")]
    [InlineData("x-netnewswire-feed:https://example.test/feed", "https://example.test/feed")]
    public void SubscriptionOptionsKeepPortsAndNativeWrappers(string input, string expected)
        => Assert.Equal(expected, Assert.Single(AppActivationRequest.FromArguments(["--subscribe", input])).Target);
}
