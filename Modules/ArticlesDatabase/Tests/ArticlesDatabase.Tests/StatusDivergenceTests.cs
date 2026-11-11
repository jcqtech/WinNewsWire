using WinNewsWire.Articles;
using Xunit;

namespace WinNewsWire.ArticlesDatabase.Tests;

/// <summary>ArticlesDatabase/Tests/ArticlesDatabaseTests/StatusDivergenceTests.swift.</summary>
public class StatusDivergenceTests : IDisposable
{
    private readonly ArticlesDatabaseStore _database = new(":memory:", "test");

    [Fact]
    public async Task StatusRepairFixesStaleReadStatusRows()
    {
        var articles = await SeedUnreadArticles(2);
        Assert.Equal(2, await _database.UnreadCountAsync(["feed1"]));
        foreach (var article in articles) article.Status.SetBoolStatus(true, ArticleStatus.Key.Read);
        Assert.Equal(2, await _database.UnreadCountAsync(["feed1"]));
        await _database.RepairStatusesAsync();
        Assert.Equal(0, await _database.UnreadCountAsync(["feed1"]));
    }

    [Fact]
    public async Task MarkRepairsStaleReadStatusRows()
    {
        var articles = await SeedUnreadArticles(2);
        foreach (var article in articles) article.Status.SetBoolStatus(true, ArticleStatus.Key.Read);
        Assert.Equal(2, await _database.UnreadCountAsync(["feed1"]));
        Assert.Empty(await _database.MarkAsync(articles.ArticleIDs(), ArticleStatus.Key.Read, true));
        Assert.Equal(0, await _database.UnreadCountAsync(["feed1"]));
    }

    [Fact]
    public async Task StatusRepairFixesStaleStarredStatusRows()
    {
        var articles = await SeedUnreadArticles(2);
        foreach (var article in articles) article.Status.SetBoolStatus(true, ArticleStatus.Key.Starred);
        Assert.Empty(await _database.FetchStarredArticleIDsAsync(["feed1"]));
        await _database.RepairStatusesAsync();
        Assert.Equal(articles.ArticleIDs(), await _database.FetchStarredArticleIDsAsync(["feed1"]));
    }

    [Fact]
    public async Task StatusRepairIsHarmlessWhenNothingIsStale()
    {
        await SeedUnreadArticles(2);
        await _database.RepairStatusesAsync();
        Assert.Equal(2, await _database.UnreadCountAsync(["feed1"]));
        Assert.Empty(await _database.FetchStarredArticleIDsAsync(["feed1"]));
    }

    [Fact]
    public async Task StatusRepairFixesStaleUnreadStatusRows()
    {
        var articles = await SeedUnreadArticles(2);
        await _database.MarkAsync(articles.ArticleIDs(), ArticleStatus.Key.Read, true);
        Assert.Equal(0, await _database.UnreadCountAsync(["feed1"]));
        foreach (var article in articles) article.Status.SetBoolStatus(false, ArticleStatus.Key.Read);
        Assert.Equal(0, await _database.UnreadCountAsync(["feed1"]));
        await _database.RepairStatusesAsync();
        Assert.Equal(2, await _database.UnreadCountAsync(["feed1"]));
    }

    [Fact]
    public async Task StatusRepairFixesMoreStatusesThanOneChunk()
    {
        const int count = 501;
        var articles = await SeedUnreadArticles(count);
        foreach (var article in articles) article.Status.SetBoolStatus(true, ArticleStatus.Key.Read);
        Assert.Equal(count, await _database.UnreadCountAsync(["feed1"]));
        await _database.RepairStatusesAsync();
        Assert.Equal(0, await _database.UnreadCountAsync(["feed1"]));
    }

    [Fact]
    public async Task NormalMarksStillReportChanges()
    {
        var articles = await SeedUnreadArticles(2);
        var ids = articles.ArticleIDs();
        Assert.Equal(ids, await _database.MarkAsync(ids, ArticleStatus.Key.Read, true));
        Assert.Equal(0, await _database.UnreadCountAsync(["feed1"]));
        Assert.Equal(ids, await _database.MarkAsync(ids, ArticleStatus.Key.Read, false));
        Assert.Equal(2, await _database.UnreadCountAsync(["feed1"]));
    }

    private async Task<IReadOnlySet<Article>> SeedUnreadArticles(int count)
    {
        var items = Enumerable.Range(1, count).Select(i => ParsedItemFixture.Create(i.ToString(), "feed1", DateTime.UtcNow, $"Article {i}"));
        var changes = await _database.UpdateAsync(items, "feed1", deleteOlder: false);
        Assert.Equal(count, changes.New!.Count);
        return changes.New;
    }

    public void Dispose() => _database.Dispose();
}
