using WinNewsWire.Core;
using Xunit;

namespace WinNewsWire.Core.Tests;

/// <summary>
/// Port of NetNewsWire RSCore's <c>String+RSCoreTests.swift</c>. Covers the
/// helpers in <see cref="StringExtensions"/> consumed by feed normalization,
/// OPML import, and the URL bar.
/// </summary>
public class StringExtensionTests
{
    [Fact]
    public void CollapsingWhitespaceRemovesRunsAndTrims()
    {
        var s = "   lots\t\tof   random\n\nwhitespace\r\n";
        Assert.Equal("lots of random whitespace", s.CollapsingWhitespace());
    }

    [Fact]
    public void TrimmingWhitespaceLeavesInteriorAlone()
    {
        var s = "   lots\t\tof   random\n\nwhitespace\r\n";
        Assert.Equal("lots\t\tof   random\n\nwhitespace", s.TrimmingWhitespace());

        Assert.Equal("foo", "\tfoo\n\n\t\r\t".TrimmingWhitespace());
        Assert.Equal(string.Empty, "\t\n\n\t\r\t".TrimmingWhitespace());
        Assert.Equal(string.Empty, "\t".TrimmingWhitespace());
        Assert.Equal(string.Empty, string.Empty.TrimmingWhitespace());
        Assert.Equal("foo", "\nfoo\n".TrimmingWhitespace());
        Assert.Equal("foo", "\nfoo".TrimmingWhitespace());
        Assert.Equal("foo", "foo\n".TrimmingWhitespace());
        Assert.Equal("fo\n\n\n\n\n\no", "fo\n\n\n\n\n\no\n".TrimmingWhitespace());
    }

    [Fact]
    public void StrippingPrefix()
    {
        Assert.Equal("bar", "foobar".StrippingPrefix("foo", caseSensitive: true));
        Assert.Equal("bar", "foobar".StrippingPrefix("FOO"));
        Assert.Equal("foobar", "foobar".StrippingPrefix("FOO", caseSensitive: true));

        Assert.Equal("foobar", "foofoobar".StrippingPrefix("foo", caseSensitive: true));
        Assert.Equal("foobar", "foofoobar".StrippingPrefix("FOO"));
        Assert.Equal("foofoobar", "foofoobar".StrippingPrefix("FOO", caseSensitive: true));

        Assert.Equal("barfoo", "barfoo".StrippingPrefix("foo", caseSensitive: true));
        Assert.Equal("barfoo", "barfoo".StrippingPrefix("FOO"));
        Assert.Equal("barfoo", "barfoo".StrippingPrefix("FOO", caseSensitive: true));
    }

    [Fact]
    public void StrippingSuffix()
    {
        Assert.Equal("foo", "foobar".StrippingSuffix("bar", caseSensitive: true));
        Assert.Equal("foo", "foobar".StrippingSuffix("BAR"));
        Assert.Equal("foobar", "foobar".StrippingSuffix("BAR", caseSensitive: true));

        Assert.Equal("foobar", "foobarbar".StrippingSuffix("bar", caseSensitive: true));
        Assert.Equal("foobar", "foobarbar".StrippingSuffix("BAR"));
        Assert.Equal("foobarbar", "foobarbar".StrippingSuffix("BAR", caseSensitive: true));

        Assert.Equal("foobar", "foobar".StrippingSuffix("foo", caseSensitive: true));
        Assert.Equal("foobar", "foobar".StrippingSuffix("FOO"));
        Assert.Equal("foobar", "foobar".StrippingSuffix("FOO", caseSensitive: true));
    }

    [Fact]
    public void EscapingSpecialXmlCharacters()
    {
        var s = "<foo attr=\"value\">bar&baz</foo>";
        Assert.Equal("&lt;foo attr=&quot;value&quot;&gt;bar&amp;baz&lt;/foo&gt;",
            s.EscapingSpecialXmlCharacters());
    }

    [Fact]
    public void StrippingHttpOrHttpsScheme()
    {
        Assert.Equal("ranchero.com/", "http://ranchero.com/".StrippingHttpOrHttpsScheme());
        Assert.Equal("ranchero.com/", "https://ranchero.com/".StrippingHttpOrHttpsScheme());
        Assert.Equal("example://ranchero.com/", "example://ranchero.com/".StrippingHttpOrHttpsScheme());
    }

    [Fact]
    public void NormalizedUrlFeedsScheme()
    {
        Assert.Equal("https://daringfireball.net/", "feeds:daringfireball.net".NormalizedUrl());
        Assert.Equal("https://daringfireball.net/", "feeds:https://daringfireball.net/".NormalizedUrl());
        Assert.Equal("https://daringfireball.net/", "feeds://https://daringfireball.net/".NormalizedUrl());
    }

    [Fact]
    public void NormalizedUrlFeedScheme()
    {
        Assert.Equal("http://daringfireball.net/", "feed:daringfireball.net".NormalizedUrl());
        Assert.Equal("https://daringfireball.net/", "feed:https://daringfireball.net/".NormalizedUrl());
        Assert.Equal("https://daringfireball.net/", "feed://https://daringfireball.net/".NormalizedUrl());
    }

    [Fact]
    public void NormalizedUrlBareHttpAndHttps()
    {
        Assert.Equal("https://daringfireball.net/", "https://daringfireball.net/".NormalizedUrl());
        Assert.Equal("http://daringfireball.net/", "http://daringfireball.net/".NormalizedUrl());
    }
}
