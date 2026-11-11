using WinNewsWire.Core;
using Xunit;

namespace WinNewsWire.Core.Tests;

/// <summary>
/// Port of NetNewsWire RSCore's <c>StripHTMLTests.swift</c>. Verifies that
/// <see cref="StringExtensions.StripHtml(string, int?)"/> removes tags, drops
/// the body of <c>&lt;script&gt;</c> and <c>&lt;style&gt;</c> blocks,
/// collapses whitespace, and honors a character-count cap.
/// </summary>
public class StripHtmlTests
{
    [Fact]
    public void StrippingHtmlBasic()
    {
        Assert.Equal("Hello world!", "<p>Hello <b>world</b>!</p>".StripHtml());
    }

    [Fact]
    public void StrippingHtmlWithScript()
    {
        Assert.Equal("Before After",
            "<p>Before</p><script>alert('test');</script><p>After</p>".StripHtml());
    }

    [Fact]
    public void StrippingHtmlWithStyle()
    {
        Assert.Equal("Content More",
            "<p>Content</p><style>body { color: red; }</style><p>More</p>".StripHtml());
    }

    [Fact]
    public void StrippingHtmlWithMaxCharacters()
    {
        var html = "<p>This is a long piece of text that should be truncated at some point.</p>";
        var result = html.StripHtml(maxCharacters: 20);
        Assert.True(result.Length <= 20);
        Assert.Equal("This is a long piece", result);
    }

    [Fact]
    public void StrippingHtmlWithUtf8()
    {
        Assert.Equal("Hello \u4e16\u754c \U0001f30d", "<p>Hello \u4e16\u754c \U0001f30d</p>".StripHtml());
    }

    [Fact]
    public void StrippingHtmlWhitespaceCollapsing()
    {
        var result = "<p>Too     many\n\n\nspaces</p>".StripHtml();
        Assert.DoesNotContain("  ", result);
        Assert.Equal("Too many spaces", result);
    }

    [Fact]
    public void StrippingHtmlWithNoTags()
    {
        Assert.Equal("Just plain text", "Just plain text".StripHtml());
    }

    [Fact]
    public void StrippingHtmlPreservesEmptyStringSemantics()
    {
        Assert.Equal(string.Empty, string.Empty.StripHtml());
    }

    [Fact]
    public void MaxCharactersOnTaglessInputTruncates()
    {
        Assert.Equal("Hello", "Hello, world!".StripHtml(maxCharacters: 5));
    }

    [Fact]
    public void ScriptAndStyleAreCaseInsensitive()
    {
        Assert.Equal("Before After", "<p>Before</p><SCRIPT>alert('x');</SCRIPT><P>After</P>".StripHtml());
        Assert.Equal("x y", "<p>x</p><STYLE>body{}</STYLE><p>y</p>".StripHtml());
        Assert.Equal("visible", "<Script>ignored</Script>visible".StripHtml());
    }

    [Fact]
    public void PlainText()
    {
        Assert.Equal("hello world", "  hello   world  ".StripHtml());
        Assert.Equal("no html here", "no html here".StripHtml());
        Assert.Equal("tabs and newlines", "\n\ttabs and\nnewlines\n".StripHtml());
    }

    [Fact]
    public void DoesNotDecodeEntities()
    {
        Assert.Equal("A &amp; B", "<p>A &amp; B</p>".StripHtml());
        Assert.Equal("&lt;3 &gt;9 &quot;q&quot;", "<p>&lt;3 &gt;9 &quot;q&quot;</p>".StripHtml());
        Assert.Equal("&#169; &#x2014;", "<p>&#169; &#x2014;</p>".StripHtml());
    }

    [Fact]
    public void EmptyString()
    {
        Assert.Equal("", "".StripHtml());
        Assert.Equal("", "".StripHtml(10));
        Assert.Equal("", "".StripHtml(0));
    }

    [Fact]
    public void BlockTagsInjectSpaces()
    {
        Assert.Equal("a b", "a<br>b".StripHtml());
        Assert.Equal("a b", "a<br/>b".StripHtml());
        Assert.Equal("a b", "a<br />b".StripHtml());
        Assert.Equal("a b", "<div>a</div><div>b</div>".StripHtml());
        Assert.Equal("a b", "<blockquote>a</blockquote><blockquote>b</blockquote>".StripHtml());
        Assert.Equal("a b", "<ul><li>a</li><li>b</li></ul>".StripHtml());
        Assert.Equal("a b", "<p>a</p><p>b</p>".StripHtml());
        Assert.Equal("a b", "<p>a</p><div>b</div>".StripHtml());
    }

    [Fact]
    public void MaxCharactersBoundaries()
    {
        Assert.Equal("hello world", "hello world".StripHtml(null));
        Assert.Equal("hello world", "hello world".StripHtml(0));
        Assert.Equal("h", "hello world".StripHtml(1));
        Assert.Equal("hello", "hello world".StripHtml(5));
        Assert.Equal("hello world", "hello world".StripHtml(100));
    }

    [Fact]
    public void NestedTags()
    {
        Assert.Equal("x", "<div><div><div>x</div></div></div>".StripHtml());
        Assert.Equal("deeply nested", "<b><i><u>deeply nested</u></i></b>".StripHtml());
        Assert.Equal("link", "<a href=\"#\"><span>link</span></a>".StripHtml());
    }

    [Fact]
    public void GreaterThanInsideQuotedAttribute()
    {
        Assert.Equal("Hello", "<source media=\"(resolution >= 2x) and (width >= 700px)\" type=\"image/avif\"><p>Hello</p>".StripHtml());
        Assert.Equal("link", "<a title=\"a > b\">link</a>".StripHtml());
        Assert.Equal("text", "<div data-x='1 > 0'>text</div>".StripHtml());
    }

    [Theory]
    [InlineData("daringfireball")]
    [InlineData("apple")]
    [InlineData("inessential")]
    [InlineData("scripting")]
    [InlineData("unsung")]
    public void WithRealWorldHtml(string testFile)
    {
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Resources", testFile + ".html"));
        var result = html.StripHtml(300);
        Assert.NotEmpty(result);
        Assert.True(result.EnumerateRunes().Count() <= 300);
        Assert.DoesNotContain("<", result);
        Assert.DoesNotContain("//", result);
    }

    [Theory]
    [InlineData("apple")]
    [InlineData("daringfireball")]
    [InlineData("inessential")]
    [InlineData("scripting")]
    [InlineData("unsung")]
    public void MatchesExpectedOutput(string testFile)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", testFile);
        Assert.Equal(File.ReadAllText(path + ".txt"), File.ReadAllText(path + ".html").StripHtml());
    }

    [Fact]
    public void LimitDoesNotSplitSurrogatePair()
        => Assert.Equal("\U0001f30d", "<p>\U0001f30d!</p>".StripHtml(1));
}
