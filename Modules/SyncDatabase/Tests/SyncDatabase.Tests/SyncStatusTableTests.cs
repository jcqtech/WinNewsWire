using Xunit;

namespace WinNewsWire.SyncDatabase.Tests;

/// <summary>SyncDatabase/Tests/SyncDatabaseTests/SyncStatusTableTests.swift.</summary>
public class SyncStatusTableTests : IDisposable
{
    private readonly SyncDatabaseStore _database = new(":memory:");

    [Fact]
    public async Task SelectForProcessingHandlesLargeQueue()
    {
        const int articleIDCount = 1200;
        var articleIDs = Enumerable.Range(0, articleIDCount).Select(i => $"article-{i}").ToList();
        var statuses = articleIDs.SelectMany(id => new[]
        {
            new SyncStatus(id, SyncStatus.SyncKey.Read, true),
            new SyncStatus(id, SyncStatus.SyncKey.Starred, true),
        });
        await _database.InsertStatusesAsync(statuses);
        Assert.Equal(articleIDCount * 2, (await _database.SelectForProcessingAsync()).Count);
        await _database.DeleteSelectedAsync(articleIDs);
        Assert.Equal(0, await _database.SelectPendingCountAsync());
    }

    [Fact]
    public async Task SelectForProcessingRespectsLimit()
    {
        await _database.InsertStatusesAsync(Enumerable.Range(0, 300)
            .Select(i => new SyncStatus($"article-{i}", SyncStatus.SyncKey.Read, true)));
        Assert.Equal(150, (await _database.SelectForProcessingAsync(limit: 150)).Count);
    }

    [Fact]
    public async Task SelectForProcessingMatchesArticleIdAndKeyPairs()
    {
        const string id = "article-with-both";
        await _database.InsertStatusesAsync([
            new(id, SyncStatus.SyncKey.Read, true),
            new(id, SyncStatus.SyncKey.Starred, true),
        ]);
        Assert.Equal(2, (await _database.SelectForProcessingAsync()).Count);
        await _database.DeleteSelectedAsync([id], SyncStatus.SyncKey.Read);
        Assert.Equal(1, await _database.SelectPendingCountAsync());
        Assert.Equal(new HashSet<string> { id }, await _database.SelectPendingArticleIDsForKeyAsync(SyncStatus.SyncKey.Starred));
    }

    [Fact]
    public async Task EmptyQueueReadsCleanly()
    {
        Assert.Empty(await _database.SelectForProcessingAsync());
        Assert.Equal(0, await _database.SelectPendingCountAsync());
        Assert.Empty(await _database.SelectPendingArticleIDsForKeyAsync(SyncStatus.SyncKey.Read));
        Assert.Empty(await _database.SelectPendingArticleIDsForKeyAsync(SyncStatus.SyncKey.Starred));
        var statuses = Enumerable.Range(0, 5).Select(i => new SyncStatus($"article-{i}", SyncStatus.SyncKey.Read, true)).ToList();
        await _database.InsertStatusesAsync(statuses);
        await _database.SelectForProcessingAsync();
        await _database.DeleteSelectedAsync(statuses.Select(s => s.ArticleID), SyncStatus.SyncKey.Read);
        Assert.Empty(await _database.SelectForProcessingAsync());
        Assert.Equal(0, await _database.SelectPendingCountAsync());
    }

    [Fact]
    public async Task AcknowledgementDoesNotDeleteNewerChange()
    {
        await _database.InsertStatusesAsync([new("article", SyncStatus.SyncKey.Read, true)]);
        await _database.SelectForProcessingAsync();
        await _database.InsertStatusesAsync([new("article", SyncStatus.SyncKey.Read, false)]);
        await _database.DeleteSelectedAsync(["article"], SyncStatus.SyncKey.Read);
        var pending = Assert.Single(await _database.SelectForProcessingAsync());
        Assert.False(pending.Flag);
    }

    [Fact]
    public async Task InFlightChangesRemainVisibleToReconciliationAndRetry()
    {
        await _database.InsertStatusesAsync([new("article", SyncStatus.SyncKey.Starred, true)]);
        await _database.SelectForProcessingAsync();
        Assert.Contains("article", await _database.SelectPendingArticleIDsForKeyAsync(SyncStatus.SyncKey.Starred));
        Assert.Single(await _database.SelectForProcessingAsync());
    }

    [Fact]
    public async Task LimitDoesNotMarkUnselectedRows()
    {
        await _database.InsertStatusesAsync([
            new("article", SyncStatus.SyncKey.Read, true),
            new("article", SyncStatus.SyncKey.Starred, true),
        ]);
        Assert.Single(await _database.SelectForProcessingAsync(limit: 1));
        await _database.DeleteSelectedAsync(["article"]);
        Assert.Equal(1, await _database.SelectPendingCountAsync());
    }

    public void Dispose() => _database.Dispose();
}
