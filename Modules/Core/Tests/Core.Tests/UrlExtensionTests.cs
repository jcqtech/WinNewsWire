using Xunit;

namespace WinNewsWire.Core.Tests;

/// <summary>RSCore/Tests/RSCoreTests/URL+RSCoreTests.swift.</summary>
public class UrlExtensionTests
{
    [Fact]
    public void PercentEncodedEmailAddressDoesNotDoubleEncode()
        => Assert.Equal("mailto:test@example.com?subject=Hello%20World",
            new Uri("mailto:test@example.com?subject=Hello%20World").PercentEncodedEmailAddress()?.AbsoluteUri);

    [Fact]
    public void PercentEncodedEmailAddressPreservesMultipleEscapes()
        => Assert.Equal("mailto:test@example.com?subject=One%20Two%20Three&body=Hi%2C%20there",
            new Uri("mailto:test@example.com?subject=One%20Two%20Three&body=Hi%2C%20there").PercentEncodedEmailAddress()?.AbsoluteUri);

    [Fact]
    public void PercentEncodedEmailAddressIsIdempotent()
    {
        var once = new Uri("mailto:test@example.com?subject=Hello%20World").PercentEncodedEmailAddress();
        Assert.Equal(once, once?.PercentEncodedEmailAddress());
    }

    [Fact]
    public void PercentEncodedEmailAddressRequiresMailto()
        => Assert.Null(new Uri("https://example.com/").PercentEncodedEmailAddress());

    [Fact]
    public void PercentEncodedEmailAddressPlainAddress()
        => Assert.Equal("mailto:test@example.com", new Uri("mailto:test@example.com").PercentEncodedEmailAddress()?.AbsoluteUri);
}
