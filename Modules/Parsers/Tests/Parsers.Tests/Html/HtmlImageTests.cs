using WinNewsWire.Parsers;
using Xunit;

namespace WinNewsWire.Parsers.Tests;

public class HtmlImageTests
{
    [Fact]
    public void FindsFirstSignificantImageAndResolvesRelativeUrls()
    {
        var html = """
            <img src="/tracking.gif" width="1" height="1">
            <img class="wp-smiley" src="/smile.png">
            <img src="photos/first.jpg" width="640" height="480">
            <img src="/second.jpg">
            """;
        Assert.Equal(new HtmlImage("https://example.com/posts/photos/first.jpg", 640, 480), First(html));
    }

    [Fact]
    public void UsesLargestSrcsetWhenSourceIsInlinePlaceholder()
    {
        var html = """<IMG SRC="data:image/gif;base64,abc" SRCSET="/small.jpg 300w, /large.jpg 1024w, /medium.jpg 768w">""";
        Assert.Equal("https://example.com/large.jpg", First(html)?.Url);
    }

    [Fact]
    public void SrcsetTakesPrecedenceOverOrdinarySource()
        => Assert.Equal("https://example.com/large.jpg",
            First("""<img src="/placeholder.jpg" srcset="/small.jpg 300w, /large.jpg 1024w">""")?.Url);

    [Fact]
    public void SharingButtonsAndGravatarsDoNotDisplaceArticlePhoto()
    {
        var html = """
            <a href="https://www.facebook.com/sharer/sharer.php?u=post"><img src="/facebook.png"></a>
            <a href="https://twitter.com/intent/tweet?url=post"><img src="/twitter.png"></a>
            <a href="mailto:?subject=Post"><img src="/email.png"></a>
            <img src="https://secure.gravatar.com/avatar/0123456789abcdef?s=96&amp;d=identicon">
            <a href="/photo-large.jpg"><img src="/photo.jpg"></a>
            """;
        Assert.Equal("https://example.com/photo.jpg", First(html)?.Url);
    }

    [Fact]
    public void FeedBurnerFlaresAndTrackingPixelAreSkipped()
    {
        var html = """
            <a href="http://feeds.feedburner.com/~ff/Example?a=abc"><img src="http://feeds.feedburner.com/~ff/Example?d=xyz"></a>
            <img src="http://feeds.feedburner.com/~r/Example/~4/xyz" height="1" width="1">
            """;
        Assert.Null(First(html));
    }

    [Theory]
    [InlineData("https://example.com/logo.svg")]
    [InlineData("https://s0.wp.com/i/blank.jpg")]
    [InlineData("https://pixel.wp.com/b.gif?v=1")]
    [InlineData("ftp://example.com/photo.jpg")]
    public void DecorativeAndNonWebUrlsAreRejected(string url)
        => Assert.False(SignificantImageFilter.IsSignificant(url, null, null));

    [Fact]
    public void BlockedHostMatchingDoesNotRejectUnrelatedDomains()
    {
        Assert.False(SignificantImageFilter.IsSignificant("https://feeds.feedburner.com/image.jpg", null, null));
        Assert.True(SignificantImageFilter.IsSignificant("https://notfeedburner.com/image.jpg", null, null));
        Assert.True(SignificantImageFilter.IsSignificant("https://gravatar.com.example.net/avatar/photo.jpg", null, null));
    }

    [Theory]
    [InlineData("https://example.com/1x1.gif")]
    [InlineData("https://example.com/PIXEL.png")]
    [InlineData("https://example.com/blank.gif")]
    [InlineData("https://example.com/spacer.gif")]
    [InlineData("https://example.com/transparent.png")]
    [InlineData("https://example.com/open/123")]
    [InlineData("https://example.com/track/event.gif")]
    public void TrackingNamesAreRejectedWithoutDeclaredDimensions(string url)
        => Assert.Null(First($"""<img src="{url}">"""));

    [Fact]
    public void UndeclaredDimensionsAreAccepted()
        => Assert.Equal(new HtmlImage("https://example.com/photo.jpg", null, null), First("""<img src="/photo.jpg">"""));

    [Fact]
    public void PixelDimensionsAreParsedButPercentagesAreNot()
        => Assert.Equal(new HtmlImage("https://example.com/photo.jpg", null, 300),
            First("""<img src="/photo.jpg" width="50%" height="300px">"""));

    [Fact]
    public void ProtocolRelativeSourceUsesDocumentScheme()
        => Assert.Equal("https://cdn.example.net/photo.jpg", First("""<img src="//cdn.example.net/photo.jpg">""")?.Url);

    [Fact]
    public void ScriptMarkupDoesNotSupplyImages()
        => Assert.Equal("https://example.com/photo.jpg",
            First("""<script>var img = '<img src="/fake.jpg">';</script><img src="/photo.jpg">""")?.Url);

    [Theory]
    [InlineData(64, 64)]
    [InlineData(400, 100)]
    public void RepresentativeSmallerAndRectangularImagesAreAccepted(int width, int height)
        => Assert.NotNull(First($"""<img src="/photo.jpg" width="{width}" height="{height}">"""));

    [Theory]
    [InlineData("""<img data-lazy-src="/photo.jpg" src="/placeholder.svg">""")]
    [InlineData("""<img data-src="/photo.jpg" src="/placeholder.svg">""")]
    [InlineData("""<img data-lazy-src="/placeholder.svg" data-src="/photo.jpg" src="/other.jpg">""")]
    [InlineData("""<img src="/placeholder.svg" srcset="/photo.jpg 1024w">""")]
    public void TriesLazySourcesAndRejectsPlaceholdersBeforeFallback(string html)
        => Assert.Equal("https://example.com/photo.jpg", First(html)?.Url);

    [Fact]
    public void MultipleCandidatesRemainUniqueAndInDocumentOrderWithinLimit()
    {
        var html = """
            <img src="/first.jpg"><img src="https://example.com/first.jpg">
            <img src="/tiny.gif" width="1" height="1"><img src="/second.jpg"><img src="/third.jpg">
            """;
        var images = HtmlImageParser.SignificantImages(html, new Uri("https://example.com/"), 2);
        Assert.Equal(["https://example.com/first.jpg", "https://example.com/second.jpg"], images.Select(image => image.Url));
    }

    [Fact]
    public void RelativeImagesAreSkippedWithoutDocumentBase()
    {
        var images = HtmlImageParser.SignificantImages(
            """<img src="/images/a.jpg"><img src="https://example.com/b.jpg">""", null, 3);
        Assert.Equal(["https://example.com/b.jpg"], images.Select(image => image.Url));
    }

    private static HtmlImage? First(string html)
        => HtmlImageParser.SignificantImages(html, new Uri("https://example.com/posts/1"), 1).FirstOrDefault();
}
