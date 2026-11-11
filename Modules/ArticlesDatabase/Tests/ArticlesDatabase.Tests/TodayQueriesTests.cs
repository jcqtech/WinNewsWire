using Xunit;
using WinNewsWire.Articles;

namespace WinNewsWire.ArticlesDatabase.Tests;

/// <summary>ArticlesDatabase/Tests/ArticlesDatabaseTests/TodayQueriesTests.swift.</summary>
public class TodayQueriesTests : IDisposable
{
    private readonly ArticlesDatabaseStore _database = new(":memory:", "test");
    private readonly Dictionary<string, string> _articleIDs = new();
    private static readonly DateTime OneHourAgo = DateTime.UtcNow.AddHours(-1);
    private static readonly DateTime TwentyFiveHoursAgo = DateTime.UtcNow.AddHours(-25);

    [Fact]
    public async Task TodayUnreadCountAcrossFeeds()
    {
        await SeedArticles();
        Assert.Equal(2, await _database.FetchUnreadCountForTodayAsync(["feedA", "feedB"]));
    }

    [Fact]
    public async Task TodayUnreadCountForOneFeed()
    {
        await SeedArticles();
        Assert.Equal(1, await _database.FetchUnreadCountForTodayAsync(["feedA"]));
        Assert.Equal(1, await _database.FetchUnreadCountForTodayAsync(["feedB"]));
    }

    [Fact]
    public async Task TodayTotalCountAcrossFeeds()
    {
        await SeedArticles();
        Assert.Equal(4, await _database.FetchTodayArticlesCountAsync(["feedA", "feedB"]));
    }

    [Fact]
    public async Task TodayTotalCountForOneFeed()
    {
        await SeedArticles();
        Assert.Equal(2, await _database.FetchTodayArticlesCountAsync(["feedA"]));
        Assert.Equal(2, await _database.FetchTodayArticlesCountAsync(["feedB"]));
    }

    [Fact]
    public async Task TodayArticlesAcrossFeeds()
    {
        await SeedArticles();
        var articles = await _database.FetchTodayArticlesAsync(["feedA", "feedB"]);
        Assert.Equal(IDs("a-recent", "a-nil", "b-recent", "b-nil"), articles.ArticleIDs());
    }

    [Fact]
    public async Task TodayArticlesForOneFeed()
    {
        await SeedArticles();
        var articles = await _database.FetchTodayArticlesAsync(["feedA"]);
        Assert.Equal(IDs("a-recent", "a-nil"), articles.ArticleIDs());
    }

    [Fact]
    public async Task TodayArticlesWithLimit()
    {
        await SeedArticles();
        var articles = await _database.FetchTodayArticlesAsync(["feedA", "feedB"], limit: 3);
        Assert.Equal(3, articles.Count);
        Assert.True(articles.ArticleIDs().IsSubsetOf(IDs("a-recent", "a-nil", "b-recent", "b-nil")));
    }

    [Fact]
    public async Task EmptyFeedIdsReturnNothing()
    {
        await SeedArticles();
        Assert.Equal(0, await _database.FetchUnreadCountForTodayAsync([]));
        Assert.Equal(0, await _database.FetchTodayArticlesCountAsync([]));
        Assert.Empty(await _database.FetchTodayArticlesAsync([]));
    }

    [Fact]
    public async Task RollingDayIncludesYesterdayButNotOlderArticles()
    {
        await _database.UpdateAsync([
            ParsedItemFixture.Create("inside", "feedA", DateTime.UtcNow.AddHours(-23.5)),
            ParsedItemFixture.Create("outside", "feedA", DateTime.UtcNow.AddHours(-24.5)),
        ], "feedA");
        Assert.Equal("inside", Assert.Single(await _database.FetchTodayArticlesAsync(["feedA"])).UniqueID);
    }

    private async Task SeedArticles()
    {
        await SeedFeed("feedA", "a");
        await SeedFeed("feedB", "b");
        var readIDs = IDs("a-recent", "b-nil");
        Assert.Equal(readIDs, await _database.MarkAsync(readIDs, ArticleStatus.Key.Read, true));
    }

    private async Task SeedFeed(string feedID, string prefix)
    {
        var changes = await _database.UpdateAsync([
            ParsedItemFixture.Create(prefix + "-recent", feedID, OneHourAgo, $"Article {prefix}-recent"),
            ParsedItemFixture.Create(prefix + "-old", feedID, TwentyFiveHoursAgo, $"Article {prefix}-old"),
            ParsedItemFixture.Create(prefix + "-nil", feedID, null, $"Article {prefix}-nil"),
        ], feedID, deleteOlder: false);
        Assert.Equal(3, changes.New!.Count);
        foreach (var article in changes.New) _articleIDs[article.UniqueID] = article.ArticleID;
    }

    private HashSet<string> IDs(params string[] uniqueIDs) => uniqueIDs.Select(id => _articleIDs[id]).ToHashSet();
    public void Dispose() => _database.Dispose();
}
