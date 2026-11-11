using Xunit;

namespace WinNewsWire.Web.Tests;

/// <summary>RSWeb/Tests/RSWebTests/SpecialCasesTests.swift.</summary>
public class SpecialCasesTests
{
    [Fact]
    public void IsRedditUrlMatchesSubdomains()
    {
        Assert.True(new Uri("https://www.reddit.com/r/swift/.rss").IsRedditUrl());
        Assert.True(new Uri("https://old.reddit.com/r/swift/.rss").IsRedditUrl());
        Assert.True(new Uri("https://np.reddit.com/r/swift/.rss").IsRedditUrl());
        Assert.True(new Uri("https://reddit.com/r/swift/.rss").IsRedditUrl());
    }

    [Fact]
    public void IsRedditUrlRejectsLookalikes()
    {
        Assert.False(new Uri("https://notreddit.com/feed").IsRedditUrl());
        Assert.False(new Uri("https://reddit.com.evil.example/feed").IsRedditUrl());
        Assert.False(new Uri("https://example.com/r/reddit.com").IsRedditUrl());
    }

    [Fact]
    public void IsYoutubeUrlMatchesSubdomains()
    {
        Assert.True(new Uri("https://www.youtube.com/watch?v=abc").IsYouTubeUrl());
        Assert.True(new Uri("https://youtube.com/feeds/videos.xml").IsYouTubeUrl());
        Assert.True(new Uri("https://m.youtube.com/watch?v=abc").IsYouTubeUrl());
    }

    [Fact]
    public void IsYoutubeUrlRejectsLookalikes()
    {
        Assert.False(new Uri("https://youtube.com.example.net/feed").IsYouTubeUrl());
        Assert.False(new Uri("https://myyoutube.community/feed").IsYouTubeUrl());
        Assert.False(new Uri("https://example.com/youtube.com").IsYouTubeUrl());
    }

    [Fact]
    public void IsRelayFMBlogUrlMatchesBlog()
    {
        Assert.True(new Uri("https://www.relay.fm/blog").IsRelayFMBlogUrl());
        Assert.True(new Uri("http://relay.fm/blog").IsRelayFMBlogUrl());
        Assert.True(new Uri("https://www.relay.fm/blog/").IsRelayFMBlogUrl());
        Assert.True(new Uri("https://www.relay.fm/blog/some-post").IsRelayFMBlogUrl());
    }

    [Fact]
    public void IsRelayFMBlogUrlRejectsNonBlog()
    {
        Assert.False(new Uri("https://relay.fm").IsRelayFMBlogUrl());
        Assert.False(new Uri("http://relay.fm/master/feed").IsRelayFMBlogUrl());
        Assert.False(new Uri("https://www.relay.fm/blogsomething").IsRelayFMBlogUrl());
        Assert.False(new Uri("https://relay.fm.evil.example/blog").IsRelayFMBlogUrl());
    }
}
