using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace WinNewsWire.NewsBlur.Tests;

public sealed class NewsBlurTests
{
    // Upstream's package testExample is an empty placeholder, retained separately
    // from the substantive Windows contract regressions below.
    [Fact]
    public void Example() { }

    [Fact]
    public void FeedResponseFiltersHiddenFeedsAndPreservesSharedMembership()
    {
        var result = JsonSerializer.Deserialize<NewsBlurFeedsResponse>("""
            {"feeds":{
                "1":{"id":1,"feed_title":"One","feed_address":"https://example.com/one"},
                "2":{"id":2,"feed_title":"Two","feed_address":"https://example.com/two"},
                "3":{"id":3,"feed_title":"Hidden","feed_address":"https://example.com/hidden"}},
             "flat_folders":{" ":[1],"Tech":[2],"Shared":[1,2],"Empty":[]}}
            """)!;
        Assert.Equal([1L, 2L], result.Feeds.Select(feed => feed.FeedID));
        Assert.Equal([1L, 2L], result.Folders.Single(folder => folder.Name == "Shared").FeedIDs);
        Assert.Empty(result.Folders.Single(folder => folder.Name == "Empty").FeedIDs);
        Assert.Equal(1L, Assert.Single(result.Folders.Single(folder => folder.Name == " ").FeedIDs));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"feeds":{}}""")]
    [InlineData("""{"feeds":{},"flat_folders":[]}""")]
    public void MissingStructureIsNotAnEmptyAccount(string json)
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<NewsBlurFeedsResponse>(json));

    [Fact]
    public void HashesAcceptNumericUnreadAndStringStarredTimestamps()
    {
        var response = JsonSerializer.Deserialize<NewsBlurStoryHashesResponse>("""
            {"unread_feed_story_hashes":{"1":[["1:unread",1700000000.25]]},
             "starred_story_hashes":[["2:starred","1700000000.5"]]}
            """)!;
        Assert.Equal(new NewsBlurStoryHash("1:unread", DateTime.UnixEpoch.AddSeconds(1700000000.25)), Assert.Single(response.Unread!));
        Assert.Equal(new NewsBlurStoryHash("2:starred", DateTime.UnixEpoch.AddSeconds(1700000000.5)), Assert.Single(response.Starred!));
    }

    [Fact]
    public void StoryTimestampIsCultureIndependentAndRetainsFractions()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var story = new NewsBlurStory("1:story", 1, null, null, null, null, "1700000000.25");
            Assert.Equal(DateTime.UnixEpoch.AddSeconds(1700000000.25), story.DatePublished);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public async Task FailedFeedRequestThrowsInsteadOfReturningAnEmptyList()
    {
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.Unauthorized)));
        await Assert.ThrowsAsync<HttpRequestException>(() => new NewsBlurAPICaller(http).RetrieveFeedsAsync());
    }

    [Fact]
    public async Task StoryRequestsFetchEveryHashInBoundedBatches()
    {
        var requested = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            var hashes = request.RequestUri!.Query.TrimStart('?').Split('&').Where(part => part.StartsWith("h="))
                .Select(part => Uri.UnescapeDataString(part[2..])).ToList();
            Assert.InRange(hashes.Count, 1, 100);
            requested.AddRange(hashes);
            return Json("""{"stories":[]}""");
        }));
        var ids = Enumerable.Range(1, 250).Select(id => $"1:{id}").ToArray();
        await new NewsBlurAPICaller(http).RetrieveStoriesByHashesAsync(ids);
        Assert.Equal(ids, requested);
    }

    private static HttpResponseMessage Json(string value)
        => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
