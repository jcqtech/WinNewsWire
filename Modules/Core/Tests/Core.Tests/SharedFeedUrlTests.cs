using Xunit;

namespace WinNewsWire.Core.Tests;

public sealed class SharedFeedUrlTests
{
    [Theory]
    [InlineData("example.com/feed", "http://example.com/feed")]
    [InlineData("//example.com/feed", "http://example.com/feed")]
    [InlineData("localhost:8080/feed", "http://localhost:8080/feed")]
    [InlineData("example.com:8443/feed", "http://example.com:8443/feed")]
    [InlineData("feeds://example.com/feed", "https://example.com/feed")]
    [InlineData("feed:https://example.com/feed", "https://example.com/feed")]
    [InlineData("  https://example.com/feed?x=a%2Bb  ", "https://example.com/feed?x=a%2Bb")]
    public void UserInputSharesActivationValidationWithoutLosingBareHosts(string input, string expected)
    {
        Assert.True(SharedFeedUrl.TryNormalizeUserInput(input, out var normalized));
        Assert.Equal(expected, normalized);
        Assert.Equal(expected, AppActivationRequest.FromArguments(["--subscribe", input]).Single().Target);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("file:///C:/private.opml")]
    [InlineData(@"C:\private.opml")]
    [InlineData(@"\\server\private.opml")]
    [InlineData("feed:file:///C:/private.opml")]
    [InlineData("feeds://file:///C:/private.opml")]
    [InlineData("javascript:alert(1)")]
    [InlineData("mailto:reader@example.com")]
    [InlineData("ftp://example.com/feed")]
    [InlineData("https://example.com/feed\nhttps://example.org/feed")]
    public void InvalidUserInputCannotBecomeAPretendHttpHost(string? input)
    {
        Assert.False(SharedFeedUrl.TryNormalizeUserInput(input, out var normalized));
        Assert.Null(normalized);
    }

    [Theory]
    [InlineData("https://example.com/feed?x=a%2Bb", "https://example.com/feed?x=a%2Bb")]
    [InlineData("HTTP://EXAMPLE.COM/feed", "http://example.com/feed")]
    [InlineData("feed://example.com/feed", "http://example.com/feed")]
    [InlineData("feeds://example.com/feed", "https://example.com/feed")]
    [InlineData("feed:https://example.com/feed", "https://example.com/feed")]
    [InlineData("x-netnewswire-feed:https://example.com/feed", "https://example.com/feed")]
    [InlineData("  https://example.com/feed  ", "https://example.com/feed")]
    public void SharedFeedAndWebsiteUrlsUseTheExistingActivationNormalization(string input, string expected)
        => Assert.Equal(expected, SharedFeedUrl.Parse(input));

    [Theory]
    [InlineData("")]
    [InlineData("example.com")]
    [InlineData("https://example.com/feed\nhttps://example.org/feed")]
    [InlineData("file:///C:/private.opml")]
    [InlineData("javascript:alert(1)")]
    [InlineData("mailto:reader@example.com")]
    [InlineData("ms-settings:")]
    [InlineData("netnewswire://theme/?url=https%3A%2F%2Fexample.com%2Ftheme.zip")]
    [InlineData("winnewswire://showunread")]
    public void OtherActivationKindsAndMultipleUrlsCannotBecomeShareSubscriptions(string input)
        => Assert.ThrowsAny<ArgumentException>(() => SharedFeedUrl.Parse(input));
}
