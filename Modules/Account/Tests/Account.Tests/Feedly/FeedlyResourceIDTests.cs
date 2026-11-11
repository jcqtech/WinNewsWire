using WinNewsWire.Feedly;
using Xunit;

namespace WinNewsWire.Account.Tests.Feedly;

public sealed class FeedlyResourceIDTests
{
    [Fact]
    public void FeedResourceID()
    {
        const string expected = "http://ranchero.com/blog/atom.xml";
        Assert.Equal(expected, FeedlyResourceIds.UrlFromFeedId("feed/" + expected));
        Assert.Equal(expected, FeedlyResourceIds.UrlFromFeedId(expected));
        Assert.Equal("whiskey/" + expected, FeedlyResourceIds.UrlFromFeedId("whiskey/" + expected));
        Assert.Equal("", FeedlyResourceIds.UrlFromFeedId(""));
    }
}
