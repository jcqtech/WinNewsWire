using System.Net;
using Xunit;

namespace WinNewsWire.Web.Tests;

/// <summary>RSWeb/Tests/RSWebTests/HTTPLinkPagingInfoTests.swift.</summary>
public class HttpLinkPagingInfoTests
{
    [Fact]
    public void ParsesNextAndLast()
    {
        var info = MakeInfo("<https://api.feedbin.com/v2/entries.json?page=2>; rel=\"next\", <https://api.feedbin.com/v2/entries.json?page=5>; rel=\"last\"");
        Assert.Equal("https://api.feedbin.com/v2/entries.json?page=2", info.NextPage);
        Assert.Equal("https://api.feedbin.com/v2/entries.json?page=5", info.LastPage);
    }

    [Fact]
    public void MalformedSegmentDoesNotCrash()
    {
        var info = MakeInfo("<https://api.feedbin.com/v2/entries.json?page=2>");
        Assert.Null(info.NextPage);
        Assert.Null(info.LastPage);
    }

    [Fact]
    public void MixedValidAndMalformedSegments()
    {
        var info = MakeInfo("<https://api.feedbin.com/v2/entries.json?page=2>; rel=\"next\", <https://api.feedbin.com/v2/entries.json?page=9>");
        Assert.Equal("https://api.feedbin.com/v2/entries.json?page=2", info.NextPage);
        Assert.Null(info.LastPage);
    }

    [Fact]
    public void NoLinkHeader()
    {
        var info = MakeInfo(null);
        Assert.Null(info.NextPage);
        Assert.Null(info.LastPage);
    }

    private static HttpLinkPagingInfo MakeInfo(string? linkHeader)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        if (linkHeader is not null) response.Headers.TryAddWithoutValidation("Link", linkHeader);
        return HttpLinkPagingInfo.FromResponse(response);
    }
}
