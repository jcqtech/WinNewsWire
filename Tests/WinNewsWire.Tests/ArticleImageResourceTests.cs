using WinNewsWire.AppShared.ArticleRendering;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

public sealed class ArticleImageResourceTests
{
    [Theory]
    [InlineData("https://images.test/a%20photo.png?x=1&y=%252F#part")]
    [InlineData("http://images.test:8080/photo.png?title=a+b")]
    public void LocalRoutePreservesTheCompleteOriginalUrlAndDecodesItOnlyOnce(string original)
    {
        Assert.True(ArticleImageResource.TryGetImageUrl(new Uri(ArticleImageResource.Url(original)), out var decoded));
        Assert.Equal(original, decoded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalRoutesRejectUnsignedAndForgedDownloads(bool tamper)
    {
        const string original = "https://images.test/allowed.png";
        const string other = "https://images.test/forged.png";
        string route = ArticleImageResource.Url(original);
        string forged = tamper
            ? route.Replace(Uri.EscapeDataString(original), Uri.EscapeDataString(other), StringComparison.Ordinal)
            : ArticleImageResource.BaseUrl + "?u=" + Uri.EscapeDataString(other);
        Assert.False(ArticleImageResource.TryGetImageUrl(new Uri(forged), out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    public void LocalRoutesRequireTheAuthenticSignature(string signature)
    {
        string route = ArticleImageResource.BaseUrl + "?u=" + Uri.EscapeDataString("https://images.test/a.png")
            + "&s=" + signature;
        Assert.False(ArticleImageResource.TryGetImageUrl(new Uri(route), out _));
    }

    [Theory]
    [InlineData("&s=extra")]
    [InlineData("&u=other")]
    [InlineData("#fragment")]
    public void SignedRoutesStillRejectAdditionalParametersAndFragments(string suffix)
    {
        string route = ArticleImageResource.Url("https://images.test/a.png");
        Assert.False(ArticleImageResource.TryGetImageUrl(new Uri(route + suffix), out _));
    }

    [Theory]
    [InlineData("https://other.test/article-image?u=https%3A%2F%2Fimages.test%2Fa.png")]
    [InlineData("http://winnewswire.local/article-image?u=https%3A%2F%2Fimages.test%2Fa.png")]
    [InlineData("https://winnewswire.local/other?u=https%3A%2F%2Fimages.test%2Fa.png")]
    [InlineData("https://winnewswire.local/article-image?u=file%3A%2F%2FC%3A%2Fsecret.png")]
    [InlineData("https://winnewswire.local/article-image?u=https%3A%2F%2Fimages.test%2Fa.png&u=other")]
    [InlineData("https://winnewswire.local/article-image?u=")]
    public void LocalRouteRejectsOtherOriginsPathsAndNonWebImages(string request)
        => Assert.False(ArticleImageResource.TryGetImageUrl(new Uri(request), out _));
}
