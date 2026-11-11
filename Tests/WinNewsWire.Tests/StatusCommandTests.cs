using WinNewsWire.AppShared.Commands;
using WinNewsWire.Articles;
using WinNewsWire.Core;
using WinNewsWire.Account;
using WinNewsWire.AppShared.Extensions;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

public class StatusCommandTests
{
    [Fact]
    public async Task MixedSelectionOnlyChangesArticlesNeedingTheTargetFlag()
    {
        var read = Article("a", "1", true);
        var unread = Article("a", "2", false);
        var applied = new List<Article>();
        var command = MarkStatusCommand.Create([read, unread], ArticleStatus.Key.Read, true, (articles, key, flag) =>
        {
            applied.AddRange(articles);
            return Task.CompletedTask;
        })!;
        await command.PerformAsync();
        Assert.Same(unread, Assert.Single(applied));
        Assert.True(unread.Status.Read);
        await command.UndoAsync();
        Assert.False(unread.Status.Read);
        Assert.True(read.Status.Read);
    }

    [Fact]
    public async Task SameArticleIdInDifferentAccountsIsNotCollapsed()
    {
        var first = Article("a", "same", false);
        var second = Article("b", "same", false);
        var command = MarkStatusCommand.Create([first, second], ArticleStatus.Key.Read, true, (articles, key, flag) =>
        {
            Assert.Equal(2, articles.Count);
            return Task.CompletedTask;
        })!;
        await command.PerformAsync();
        Assert.True(first.Status.Read);
        Assert.True(second.Status.Read);
    }

    [Fact]
    public async Task FailedWriteDoesNotMutateStatusOrRegisterUndo()
    {
        var article = Article("a", "1", false);
        var history = new UndoManager();
        var command = MarkStatusCommand.Create([article], ArticleStatus.Key.Read, true,
            (_, _, _) => Task.FromException(new IOException("disk full")))!;
        await Assert.ThrowsAsync<IOException>(() => history.ExecuteAsync(command));
        Assert.False(article.Status.Read);
        Assert.False(history.CanUndo);
        Assert.False(history.IsExecuting);
    }

    [Fact]
    public async Task UndoFailureKeepsTheCommandAvailableForRetry()
    {
        var article = Article("a", "1", false);
        bool fail = false;
        var command = MarkStatusCommand.Create([article], ArticleStatus.Key.Read, true,
            (_, _, _) => fail ? Task.FromException(new IOException("offline")) : Task.CompletedTask)!;
        var history = new UndoManager();
        await history.ExecuteAsync(command);
        fail = true;
        await Assert.ThrowsAsync<IOException>(history.UndoAsync);
        Assert.True(history.CanUndo);
        Assert.True(article.Status.Read);
        fail = false;
        await history.UndoAsync();
        Assert.False(article.Status.Read);
        Assert.True(history.CanRedo);
        await history.RedoAsync();
        Assert.True(article.Status.Read);
    }

    [Fact]
    public async Task NewCommandClearsRedoHistory()
    {
        var article = Article("a", "1", false);
        var history = new UndoManager();
        Task Apply(IReadOnlyCollection<Article> articles, ArticleStatus.Key key, bool value) => Task.CompletedTask;
        await history.ExecuteAsync(MarkStatusCommand.Create([article], ArticleStatus.Key.Read, true, Apply)!);
        await history.UndoAsync();
        await history.ExecuteAsync(MarkStatusCommand.Create([article], ArticleStatus.Key.Starred, true, Apply)!);
        Assert.False(history.CanRedo);
        Assert.Equal("Mark Starred", history.UndoActionName);
    }

    private static Article Article(string account, string id, bool read)
        => new(account, id, "feed", id, "Title", null, null, null, null, null, null, null,
            null, null, null, null, new ArticleStatus(id, read, false, DateTime.UnixEpoch));

    [Fact]
    public async Task MarkUnreadOmitsArticlesOutsideTheServicesWindow()
    {
        var old = Article("feedly", "old", true);
        var local = Article("local", "old", true);
        var applied = new List<Article>();
        var now = DateTime.UnixEpoch.AddDays(32);
        var command = MarkStatusCommand.Create([old, local], ArticleStatus.Key.Read, false,
            (articles, _, _) => { applied.AddRange(articles); return Task.CompletedTask; },
            article => article.IsAvailableToMarkUnread(article.AccountID == "feedly" ? AccountType.Feedly : AccountType.OnMyMac, now))!;
        await command.PerformAsync();
        Assert.Same(local, Assert.Single(applied));
        Assert.True(old.Status.Read);
        Assert.False(local.Status.Read);
    }

    [Theory]
    [InlineData(30, true)]
    [InlineData(31, false)]
    [InlineData(32, false)]
    public void MarkUnreadWindowUsesTheExactBoundary(int age, bool expected)
        => Assert.Equal(expected, Article("feedly", "article", true).IsAvailableToMarkUnread(AccountType.Feedly, DateTime.UnixEpoch.AddDays(age)));

    [Fact]
    public async Task PartiallyAppliedStatusesAreRolledBackBeforeReportingFailure()
    {
        var first = Article("one", "1", false);
        var second = Article("two", "2", false);
        var command = MarkStatusCommand.Create([first, second], ArticleStatus.Key.Read, true, (articles, key, value) =>
        {
            if (value)
            {
                first.Status.SetBoolStatus(true, key);
                throw new IOException("The second account could not be updated.");
            }
            foreach (var article in articles) article.Status.SetBoolStatus(false, key);
            return Task.CompletedTask;
        })!;
        var history = new UndoManager();
        await Assert.ThrowsAsync<IOException>(() => history.ExecuteAsync(command));
        Assert.False(first.Status.Read);
        Assert.False(second.Status.Read);
        Assert.False(history.CanUndo);
    }
}
