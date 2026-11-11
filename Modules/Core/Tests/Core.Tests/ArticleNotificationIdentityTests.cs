using Xunit;

namespace WinNewsWire.Core.Tests;

public sealed class ArticleNotificationIdentityTests
{
    [Theory]
    [InlineData("one", "article")]
    [InlineData("account+two", "article/id?key=one&value=two%2F")]
    [InlineData("account\u00E9", "\u6587\u7AE0")]
    public void NotificationLinksRoundTripThroughActivation(string account, string article)
    {
        var request = AppActivationRequest.FromUri(ArticleNotificationIdentity.ActivationUri(account, article));
        Assert.Equal(AppActivationKind.ShowUnread, request.Kind);
        Assert.Equal(account, request.AccountID);
        Assert.Equal(article, request.ArticleID);
    }

    [Fact]
    public void TagsAreStableBoundedAndScopedToAccounts()
    {
        string article = new('x', 400);
        string first = ArticleNotificationIdentity.Tag("one", article);
        Assert.Equal(64, first.Length);
        Assert.Equal(first, ArticleNotificationIdentity.Tag("one", article));
        Assert.NotEqual(first, ArticleNotificationIdentity.Tag("two", article));
    }

    [Fact]
    public void SeparatorsDoNotMakeDistinctIdentitiesCollide()
        => Assert.NotEqual(ArticleNotificationIdentity.Tag("one|two", "three"),
            ArticleNotificationIdentity.Tag("one", "two|three"));

    [Theory]
    [InlineData("", "article")]
    [InlineData("account", "")]
    public void EmptyIdentitiesCannotProduceNotificationArguments(string account, string article)
        => Assert.Throws<ArgumentException>(() => ArticleNotificationIdentity.ActivationUri(account, article));

    [Fact]
    public void ExistingNotificationArgumentsOpenTheExactAccountArticle()
    {
        var request = AppActivationRequest.FromNotification(new Dictionary<string, string>
        {
            ["articleID"] = "item-1",
            ["accountID"] = "account-2",
            ["feedID"] = "feed-3"
        });
        Assert.Equal(new(AppActivationKind.ShowUnread, ArticleID: "item-1", AccountID: "account-2"), request);
    }

    [Fact]
    public void IncompleteArticleNotificationDoesNotGuessAnAccount()
        => Assert.Throws<ArgumentException>(() => AppActivationRequest.FromNotification(
            new Dictionary<string, string> { ["articleID"] = "item" }));

    [Fact]
    public void ANotificationWithoutAnArticleShowsTheApplication()
        => Assert.Equal(new(AppActivationKind.ShowMainWindow), AppActivationRequest.FromNotification(new Dictionary<string, string>()));
}
