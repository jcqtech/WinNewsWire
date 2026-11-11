using WinNewsWire.Feedly;
using Xunit;

namespace WinNewsWire.Account.Tests.Feedly;

public sealed class FeedlyActivityMessageTests
{
    [Fact]
    public void SendStatusMessageCounts()
    {
        Assert.Equal("No statuses to send", FeedlyAccountDelegate.SendStatusMessage(0));
        Assert.Equal("1 status sent", FeedlyAccountDelegate.SendStatusMessage(1));
        Assert.Equal("2 statuses sent", FeedlyAccountDelegate.SendStatusMessage(2));
    }

    [Fact]
    public void RefreshStatusMessageWithNoChanges()
        => Assert.Equal("No changes", FeedlyAccountDelegate.RefreshStatusMessage(new()));

    [Fact]
    public void RefreshStatusMessageJoinsPartsInOrder()
        => Assert.Equal("3 marked unread, 2 marked read, 1 starred, 4 unstarred", FeedlyAccountDelegate.RefreshStatusMessage(new(3, 2, 1, 4)));

    [Fact]
    public void FeedListMessageWithNoChanges()
        => Assert.Equal("No changes", FeedlyAccountDelegate.FeedListMessage(new()));

    [Fact]
    public void FeedListMessagePluralizes()
    {
        Assert.Equal("1 folder added, 1 feed added", FeedlyAccountDelegate.FeedListMessage(new(FoldersAdded: 1, FeedsAdded: 1)));
        Assert.Equal("2 folders removed, 3 feeds removed, 2 feeds renamed", FeedlyAccountDelegate.FeedListMessage(new(FoldersRemoved: 2, FeedsRemoved: 3, FeedsRenamed: 2)));
    }

    [Fact]
    public void RefreshAllMessageWithNoChanges()
        => Assert.Equal("No changes", FeedlyAccountDelegate.RefreshAllMessage(new()));

    [Fact]
    public void RefreshAllMessageComposesAllPartsInOrder()
    {
        var summary = new FeedlyAccountDelegate.RefreshAllSummary
        {
            ArticlesDownloaded = 1, NewArticlesFromFeedRefresh = 2, StatusRefreshCounts = new(UnreadAdded: 3),
            StatusesSent = 1, FeedListChanges = new(FeedsAdded: 5),
        };
        Assert.Equal("1 article downloaded, 2 new from feed refresh, 3 marked unread, 1 status sent, 5 feeds added",
            FeedlyAccountDelegate.RefreshAllMessage(summary));
    }
}
