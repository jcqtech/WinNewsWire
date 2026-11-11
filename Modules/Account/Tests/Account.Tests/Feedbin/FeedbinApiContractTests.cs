using System.Net;
using System.Text;
using System.Text.Json;
using WinNewsWire.Feedbin;
using Xunit;

namespace WinNewsWire.Account.Tests.Feedbin;

public class FeedbinApiContractTests
{
    [Fact]
    public async Task SubscriptionsFollowNextPageLinks()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++;
            var response = Json($$"""[{"id":{{calls}},"feed_id":{{calls}},"title":"Feed","feed_url":"https://example.com/{{calls}}"}]""");
            if (calls == 1)
            {
                Assert.Equal("?mode=extended", request.RequestUri!.Query);
                response.Headers.TryAddWithoutValidation("Link", "<https://api.feedbin.com/v2/subscriptions.json?page=2>; rel=\"next\"");
            }
            else
                Assert.Equal("?page=2", request.RequestUri!.Query);
            return response;
        }));
        Assert.Equal(2, (await new FeedbinAPICaller(http).RetrieveSubscriptionsAsync()).Items!.Count);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("https://other.example/subscriptions.json")]
    [InlineData("https://api.feedbin.com/v2/subscriptions.json?mode=extended")]
    public async Task PaginationRejectsForeignOrRepeatedLinks(string next)
    {
        int calls = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            calls++;
            var response = Json("[]");
            response.Headers.TryAddWithoutValidation("Link", $"<{next}>; rel=\"next\"");
            return response;
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new FeedbinAPICaller(http).RetrieveSubscriptionsAsync());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task EntryRequestsDoNotSilentlyDiscardIdsPastOneHundred()
    {
        var requested = new List<long>();
        using var http = new HttpClient(new Handler(request =>
        {
            var batch = request.RequestUri!.Query["?ids=".Length..].Split(',').Select(long.Parse).ToList();
            Assert.InRange(batch.Count, 1, 100);
            requested.AddRange(batch);
            return Json("[]");
        }));
        var ids = Enumerable.Range(1, 250).Select(i => (long)i).ToList();
        await new FeedbinAPICaller(http).RetrieveEntriesAsync(ids);
        Assert.Equal(ids, requested);
    }

    [Fact]
    public async Task FailedListRequestDoesNotLookLikeAnEmptyAccount()
    {
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        await Assert.ThrowsAsync<HttpRequestException>(() => new FeedbinAPICaller(http).RetrieveSubscriptionsAsync());
    }

    [Fact]
    public async Task UnsolicitedNotModifiedIsNotAnEmptySnapshot()
    {
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotModified)));
        await Assert.ThrowsAsync<InvalidDataException>(() => new FeedbinAPICaller(http).RetrieveSubscriptionsAsync());
    }

    [Fact]
    public async Task PaginatedSnapshotsCannotReuseTheFirstPagesValidator()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            var response = Json("[]");
            response.Headers.TryAddWithoutValidation("ETag", "\"page-1\"");
            if (++calls == 1)
                response.Headers.TryAddWithoutValidation("Link", "<https://api.feedbin.com/v2/subscriptions.json?page=2>; rel=\"next\"");
            return response;
        }));
        var response = await new FeedbinAPICaller(http).RetrieveSubscriptionsAsync();
        Assert.NotNull(response.Items);
        Assert.Null(response.ConditionalGetInfo);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(0, "DELETE", "unread_entries")]
    [InlineData(1, "POST", "unread_entries")]
    [InlineData(2, "POST", "starred_entries")]
    [InlineData(3, "DELETE", "starred_entries")]
    public async Task StatusChangesUseNativeMethodsAndThousandItemBatches(int action, string method, string key)
    {
        var sent = new List<long>();
        int calls = 0;
        using var http = new HttpClient(new AsyncHandler(async request =>
        {
            calls++;
            Assert.Equal(method, request.Method.Method);
            Assert.Equal("/v2/" + key + ".json", request.RequestUri!.AbsolutePath);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var ids = json.RootElement.GetProperty(key).EnumerateArray().Select(item => item.GetInt64()).ToArray();
            Assert.InRange(ids.Length, 1, 1000);
            sent.AddRange(ids);
            return Json("{}");
        }));
        var api = new FeedbinAPICaller(http);
        var ids = Enumerable.Range(1, 1001).Select(id => (long)id).ToArray();
        await (action switch
        {
            0 => api.MarkAsReadAsync(ids),
            1 => api.MarkAsUnreadAsync(ids),
            2 => api.MarkAsStarredAsync(ids),
            _ => api.MarkAsUnstarredAsync(ids)
        });
        Assert.Equal(ids, sent);
        Assert.Equal(2, calls);
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request);
    }
}
