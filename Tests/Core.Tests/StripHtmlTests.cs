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
}
