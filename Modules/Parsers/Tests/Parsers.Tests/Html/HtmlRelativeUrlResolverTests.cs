using System.Text;
using Xunit;

namespace WinNewsWire.Parsers.Tests;

public class HtmlRelativeUrlResolverTests
{
    private static readonly Uri Base = new("https://example.com/blog/post1/");
    private static string Resolve(string html) => HtmlRelativeUrlResolver.Resolve(html, Base);

    [Fact]
    public void RelativeValuesResolve()
    {
        var cases = new[]
        {
            ("<a href=\"page.html\">x</a>", "<a href=\"https://example.com/blog/post1/page.html\">x</a>"),
            ("<img src=\"img.png\">", "<img src=\"https://example.com/blog/post1/img.png\">"),
            ("<video poster=\"p.jpg\"></video>", "<video poster=\"https://example.com/blog/post1/p.jpg\"></video>"),
            ("<a href=\"/root\">x</a>", "<a href=\"https://example.com/root\">x</a>"),
            ("<a href=\"../up/\">x</a>", "<a href=\"https://example.com/blog/up/\">x</a>"),
            ("<a href=\"?q=1\">x</a>", "<a href=\"https://example.com/blog/post1/?q=1\">x</a>"),
            ("<a href=\"//cdn.example.com/x\">x</a>", "<a href=\"https://cdn.example.com/x\">x</a>")
        };
        foreach (var (input, expected) in cases) Assert.Equal(expected, Resolve(input));
    }

    [Fact]
    public void AmpersandEntityInRelativeUrlSurvives()
        => Assert.Equal("<a href=\"https://example.com/blog/post1/page?a=1&amp;b=2\">x</a>", Resolve("<a href=\"page?a=1&amp;b=2\">x</a>"));

    [Fact]
    public void AttributeNamesAreCaseInsensitive()
    {
        Assert.Equal("<a HREF=\"https://example.com/blog/post1/x\">y</a>", Resolve("<a HREF=\"x\">y</a>"));
        Assert.Equal("<img SrcSet=\"https://example.com/blog/post1/x 1x\">", Resolve("<img SrcSet=\"x 1x\">"));
    }

    [Fact]
    public void UnquotedAndSingleQuotedValuesResolve()
    {
        Assert.Equal("<a href=https://example.com/blog/post1/foo/bar>x</a>", Resolve("<a href=foo/bar>x</a>"));
        Assert.Equal("<a href='https://example.com/blog/post1/x'>y</a>", Resolve("<a href='x'>y</a>"));
    }

    [Fact]
    public void UntouchedValues()
    {
        foreach (var html in new[]
        {
            "<a href=\"#fn1\">x</a>", "<a href=\"\">x</a>",
            "<a href=\"https://other.example.com/a\">x</a>", "<a href=\"http://other.example.com/a\">x</a>",
            "<img src=\"data:image/png;base64,AAAA\">", "<a href=\"mailto:x@example.com\">x</a>",
            "<a href=\"javascript:void(0)\">x</a>", "<a title=\"foo.png\" data-src=\"x\">not URL attributes</a>"
        }) Assert.Equal(html, Resolve(html));
    }

    [Fact]
    public void ScriptStyleCommentsAndCdataUntouched()
    {
        foreach (var html in new[]
        {
            "<script>var a = \"<img src=foo>\";</script>",
            "<style>.x { background: url(foo.png); }</style>",
            "<!-- <a href=\"x\">commented out</a> -->", "<![CDATA[<a href=\"x\">]]>"
        }) Assert.Equal(html, Resolve(html));
    }

    [Fact]
    public void ScriptSrcAttributeStillResolves()
        => Assert.Equal("<script src=\"https://example.com/blog/post1/app.js\">var s = \"<img src=x>\";</script>",
            Resolve("<script src=\"app.js\">var s = \"<img src=x>\";</script>"));

    [Fact]
    public void SrcsetCandidatesResolve()
    {
        var cases = new[]
        {
            ("<img srcset=\"a.png\">", "<img srcset=\"https://example.com/blog/post1/a.png\">"),
            ("<img srcset=\"a.png 1x, b.png 2x\">", "<img srcset=\"https://example.com/blog/post1/a.png 1x, https://example.com/blog/post1/b.png 2x\">"),
            ("<img srcset=\"a.png, b.png 2x\">", "<img srcset=\"https://example.com/blog/post1/a.png, https://example.com/blog/post1/b.png 2x\">"),
            ("<img srcset=\"a.png 1x,b.png 2x\">", "<img srcset=\"https://example.com/blog/post1/a.png 1x,https://example.com/blog/post1/b.png 2x\">"),
            ("<img srcset=\"https://other.example.com/a.png 1x, b.png 2x\">", "<img srcset=\"https://other.example.com/a.png 1x, https://example.com/blog/post1/b.png 2x\">")
        };
        foreach (var (input, expected) in cases) Assert.Equal(expected, Resolve(input));
    }

    [Fact]
    public void SurroundingWhitespaceInValuesIsIgnoredAndPreserved()
    {
        Assert.Equal("<a href=\" #fn1\">x</a>", Resolve("<a href=\" #fn1\">x</a>"));
        Assert.Equal("<a href=\" https://example.com/blog/post1/page.html \">x</a>", Resolve("<a href=\" page.html \">x</a>"));
    }

    [Fact]
    public void SimilarlyNamedCloseTagDoesNotEndRawText()
    {
        const string html = "<script>if (a</scripty>b) { s = '<img src=\"x.png\">'; }</script>";
        Assert.Equal(html, Resolve(html));
    }

    [Theory]
    [InlineData("textarea")]
    [InlineData("TITLE")]
    [InlineData("xmp")]
    [InlineData("noembed")]
    public void ImagesWrittenAsTextAreLeftAlone(string tag)
    {
        var html = $"<{tag}>Embed: <img src=\"photo.jpg\"></{tag}><img src=\"photo.jpg\">";
        var expected = $"<{tag}>Embed: <img src=\"photo.jpg\"></{tag}><img src=\"https://example.com/blog/post1/photo.jpg\">";
        Assert.Equal(expected, Resolve(html));
    }

    [Fact]
    public void MalformedInputIsSafe()
    {
        const string unterminated = "<a href=\"never closed";
        Assert.Equal(unterminated, Resolve(unterminated));
        Assert.Equal("<img src=\"https://example.com/blog/post1/a.png\"><a href=\"never closed",
            Resolve("<img src=\"a.png\"><a href=\"never closed"));
        Assert.Equal("<a href=", Resolve("<a href="));
        Assert.Equal("1 < 2 and <a href=\"https://example.com/blog/post1/x\">y</a>", Resolve("1 < 2 and <a href=\"x\">y</a>"));
    }

    [Fact]
    public void AllAbsoluteDocumentIsUnchanged()
    {
        const string html = """
            <p>Intro text with an <a href="https://example.org/a">absolute link</a>.</p>
            <img src="https://example.org/img.png" srcset="https://example.org/img.png 1x">
            <p>More text, an <a href="#fn1">footnote</a>, and an <a href="">empty href</a>.</p>
            """;
        Assert.Equal(html, Resolve(html));
    }

    [Fact]
    public void MixedDocumentIsSurgical()
    {
        const string html = "<figure class='pic'>\n\t<picture>\n\t\t<source srcset=\"shot_light.png\" media=\"(prefers-color-scheme: light)\" width=\"1280\" height=\"720\"/>\n\t\t<img src=\"shot.png\" width=\"1280\" height=\"720\" alt=\"A screenshot\"/>\n\t</picture>\n\t<figcaption>Caption &amp; more</figcaption>\n</figure>";
        const string expected = "<figure class='pic'>\n\t<picture>\n\t\t<source srcset=\"https://example.com/blog/post1/shot_light.png\" media=\"(prefers-color-scheme: light)\" width=\"1280\" height=\"720\"/>\n\t\t<img src=\"https://example.com/blog/post1/shot.png\" width=\"1280\" height=\"720\" alt=\"A screenshot\"/>\n\t</picture>\n\t<figcaption>Caption &amp; more</figcaption>\n</figure>";
        Assert.Equal(expected, Resolve(html));
    }

    [Fact]
    public void BytesEntryPointMatchesStringEntryPoint()
    {
        const string html = "<img src=\"img.png\">";
        Assert.Equal(Resolve(html), HtmlRelativeUrlResolver.Resolve(Encoding.UTF8.GetBytes(html), Base));
        const string absolute = "<img src=\"https://example.org/img.png\">";
        Assert.Equal(absolute, HtmlRelativeUrlResolver.Resolve(Encoding.UTF8.GetBytes(absolute), Base));
    }
}
