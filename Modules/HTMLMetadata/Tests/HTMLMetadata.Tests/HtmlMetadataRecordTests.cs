using System.Text;
using WinNewsWire.Parsers;
using Xunit;

namespace WinNewsWire.HTMLMetadata.Tests;

public sealed class HtmlMetadataRecordTests
{
    [Fact]
    public void ParserProjectionKeepsAllNativeMetadataFields()
    {
        var metadata = HtmlMetadataParser.Parse(new ParserData("https://example.test/news/", Encoding.UTF8.GetBytes("""
            <head><link rel="icon" type="image/png" href="../icon.png">
            <link rel="apple-touch-icon" sizes="180x180" href="/touch.png">
            <link rel="alternate" title="News" type="application/rss+xml" href="/rss">
            <meta property="og:image" content="https://example.test/og.png">
            <meta property="og:image:width" content="600"><meta property="og:image:height" content="500">
            <meta name="twitter:image:src" content="https://example.test/twitter.png"></head>
            """)));
        var record = new HtmlMetadataRecord("https://original.test/", metadata);
        Assert.Equal("https://original.test/", record.Url);
        Assert.Equal("https://example.test/icon.png", Assert.Single(record.Favicons).UrlString);
        Assert.Equal(180, Assert.Single(record.AppleTouchIcons).Width);
        Assert.Equal("News", Assert.Single(record.FeedLinks).Title);
        Assert.Equal(600, Assert.Single(record.OpenGraphImages).Width);
        Assert.Equal("https://example.test/twitter.png", record.TwitterImageUrl);
    }

    [Fact]
    public void IconRankingMatchesNativeAspectRatioAndStrictDimensions()
    {
        var record = new HtmlMetadataRecord("https://example.test/", appleTouchIcons:
        [
            new(null, null, 900, 100, "banner"),
            new(null, null, 100, 100, "first"),
            new(null, null, 150, 100, "same-height"),
            new(null, null, 200, 200, "larger")
        ]);
        Assert.Equal("larger", record.BestHomePageIconUrl());
    }

    [Fact]
    public void SecureOpenGraphFallsBackToTwitterForWordPressPlaceholder()
    {
        var record = new HtmlMetadataRecord("https://example.test/", openGraphImages:
            [new("http://example.test/icon.png", "https://example.test/icon.png", null, 100, 100, null)]);
        Assert.Equal("https://example.test/icon.png", record.BestHomePageIconUrl());
        record = new("https://example.test/", openGraphImages:
            [new("https://s0.wp.com/i/blank.jpg", null, null, 100, 100, null)], twitterImageUrl: "twitter");
        Assert.Equal("twitter", record.BestHomePageIconUrl());
    }

    [Fact]
    public void FaviconDiscoveryRejectsNonWebAndSvgCandidates()
    {
        var record = new HtmlMetadataRecord("https://example.test/", favicons:
        [
            new("image/svg+xml", "https://example.test/no-extension"),
            new(null, "https://example.test/icon.svg"),
            new(null, "data:image/png;base64,AAAA"),
            new("image/png", "https://example.test/icon.png")
        ]);
        Assert.Equal(["https://example.test/icon.png"], record.UsableFaviconUrls());
    }

    [Fact]
    public void MetadataArraysAreOwnedSnapshots()
    {
        var favicons = new List<HtmlMetadataRecord.Favicon> { new(null, "https://example.test/one") };
        var record = new HtmlMetadataRecord("https://example.test/", favicons);
        favicons.Clear();
        Assert.Single(record.Favicons);
        Assert.Throws<NotSupportedException>(() => ((IList<HtmlMetadataRecord.Favicon>)record.Favicons).Clear());
    }
}
