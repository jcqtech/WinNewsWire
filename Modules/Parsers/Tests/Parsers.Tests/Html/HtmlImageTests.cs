using System.Text;
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

    private static HtmlImage? First(string html)
        => HtmlImageParser.FirstSignificantImage(new ParserData("https://example.com/posts/1", Encoding.UTF8.GetBytes(html)));
}
