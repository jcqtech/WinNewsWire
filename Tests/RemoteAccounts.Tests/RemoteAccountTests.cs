using System.Text.Json;
using WinNewsWire.Account;
using WinNewsWire.Feedbin;
using WinNewsWire.NewsBlur;
using WinNewsWire.ReaderAPI;
using Xunit;

namespace RemoteAccounts.Tests;

public class RemoteAccountTests
{
    [Fact]
    public void Feedbin_DelegateReportsType()
        => Assert.Equal(AccountType.Feedbin, new FeedbinAccountDelegate().Type);

    [Fact]
    public void NewsBlur_DelegateReportsType()
        => Assert.Equal(AccountType.NewsBlur, new NewsBlurAccountDelegate().Type);

    [Theory]
    [InlineData(ReaderAPIVariant.FreshRSS, AccountType.FreshRSS)]
    [InlineData(ReaderAPIVariant.Inoreader, AccountType.Inoreader)]
    [InlineData(ReaderAPIVariant.BazQux, AccountType.BazQux)]
    [InlineData(ReaderAPIVariant.TheOldReader, AccountType.TheOldReader)]
    public void ReaderAPI_VariantMapsToAccountType(ReaderAPIVariant variant, AccountType expected)
    {
        var del = new ReaderAPIAccountDelegate(variant);
        Assert.Equal(expected, del.Type);
    }

    [Fact]
    public void FeedbinEntry_DeserializesSnakeCaseKeys()
    {
        var json = """
        {"id":123,"feed_id":42,"title":"Hello","url":"https://example.com/a",
         "author":"Alice","content":"<p>Hi</p>","summary":"greeting",
         "published":"2024-01-02T03:04:05Z","created_at":"2024-01-02T03:04:06Z"}
        """;
        var entry = JsonSerializer.Deserialize<FeedbinEntry>(json);
        Assert.NotNull(entry);
        Assert.Equal(123, entry!.ArticleID);
        Assert.Equal(42, entry.FeedID);
        Assert.Equal("Hello", entry.Title);
        Assert.Equal("<p>Hi</p>", entry.ContentHtml);
    }

    [Fact]
    public void NewsBlurStory_ParsesUnixTimestamp()
    {
        var json = """
        {"story_hash":"abc","story_feed_id":7,"story_title":"t","story_permalink":"u",
         "story_authors":"a","story_content":"c","story_timestamp":"1704160000"}
        """;
        var s = JsonSerializer.Deserialize<NewsBlurStory>(json);
        Assert.NotNull(s);
        Assert.NotNull(s!.DatePublished);
        Assert.Equal(2024, s.DatePublished!.Value.Year);
    }

    [Fact]
    public void AccountDelegateFactory_ResolvesRegisteredResolver()
    {
        var marker = new LocalAccountDelegate();
        var prev = AccountDelegateFactory.Resolver;
        try
        {
            AccountDelegateFactory.Resolver = (_, _) => marker;
            Assert.Same(marker, AccountDelegateFactory.Resolve(AccountType.Feedbin, "id"));
        }
        finally { AccountDelegateFactory.Resolver = prev; }
    }
}
