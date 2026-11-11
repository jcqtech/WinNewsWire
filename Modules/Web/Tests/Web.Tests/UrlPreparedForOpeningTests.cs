using Xunit;

namespace WinNewsWire.Web.Tests;

/// <summary>RSWeb/Tests/RSWebTests/URLPreparedForOpeningTests.swift.</summary>
public class UrlPreparedForOpeningTests
{
    [Fact]
    public void AllowsWebAndMailSchemes()
    {
        Assert.NotNull(new Uri("http://example.com/").PreparedForOpeningInBrowser());
        Assert.NotNull(new Uri("https://example.com/").PreparedForOpeningInBrowser());
        Assert.NotNull(new Uri("mailto:me@example.com").PreparedForOpeningInBrowser());
    }

    [Fact]
    public void BlocksOtherSchemes()
    {
        Assert.Null(new Uri("smb://server/share").PreparedForOpeningInBrowser());
        Assert.Null(new Uri("file:///etc/passwd").PreparedForOpeningInBrowser());
        Assert.Null(new Uri("ftp://example.com/x").PreparedForOpeningInBrowser());
        Assert.Null(new Uri("javascript:alert(1)").PreparedForOpeningInBrowser());
        Assert.Null(new Uri("data:text/html,x").PreparedForOpeningInBrowser());
        Assert.Null(new Uri("tel:+15555555555").PreparedForOpeningInBrowser());
    }

    [Fact]
    public void SchemeMatchIsCaseInsensitive()
    {
        Assert.NotNull(new Uri("HTTPS://example.com/").PreparedForOpeningInBrowser());
        Assert.NotNull(new Uri("MailTo:me@example.com").PreparedForOpeningInBrowser());
    }

    [Fact]
    public void AllowedUrlPassesThroughUnchanged()
        => Assert.Equal("http://example.com/path?q=1", new Uri("http://example.com/path?q=1").PreparedForOpeningInBrowser()?.AbsoluteUri);
}
