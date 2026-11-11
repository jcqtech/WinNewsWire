using WinNewsWire.Core;
using Xunit;

namespace WinNewsWire.Core.Tests;

public class UrlNormalizationTests
{
    [Theory]
    [InlineData("https://beautifulpixels.com", "https://beautifulpixels.com/")]
    [InlineData("  https://example.com  ", "https://example.com/")]
    [InlineData("example.com", "http://example.com/")]
    [InlineData("//example.com", "http://example.com/")]
    [InlineData("feeds:example.com", "https://example.com/")]
    [InlineData("FEED:https://example.com", "https://example.com/")]
    [InlineData("feed:http://boingboing.net/feed", "http://boingboing.net/feed")]
    [InlineData("https://example.com/path", "https://example.com/path")]
    public void NormalizationMatchesSource(string input, string expected)
        => Assert.Equal(expected, input.NormalizedUrl());
}
