using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WinNewsWire.Articles;
using WinNewsWire.Parsers;
using Xunit;

namespace WinNewsWire.ArticlesDatabase.Tests;

public sealed class ParserIdentityMigrationTests
{
    private const string FeedUrl = "https://example.com/feed.atom";
    private const string FeedId = "feed";

    public static IEnumerable<object[]> LegacyCases()
    {
        yield return
        [
            """
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns="http://purl.org/rss/1.0/">
              <channel><title>Feed</title><link>https://example.com/</link></channel>
              <item rdf:about="https://example.com/article"><title>Article</title><link>https://example.com/article</link><description>Fresh body</description></item>
            </rdf:RDF>
            """,
            Md5("https://example.com/article")
        ];
        yield return
        [
            """
            <rss version="2.0"><channel><title>Feed</title><link>https://example.com/</link>
              <item><title>Article</title><pubDate>2020-05-01T00:00:00.750Z</pubDate><description>Fresh body</description></item>
            </channel></rss>
            """,
            Md5("Article1588291200")
        ];
        yield return
        [
            """
            <feed xmlns="http://www.w3.org/2005/Atom" xml:base="https://example.com/posts/"><title>Feed</title>
              <entry><title>Article</title><link href="article"/><content>Fresh body</content></entry>
            </feed>
            """,
            "https://example.com/article"
        ];
        yield return
        [
            """
            <feed xmlns="http://www.w3.org/2005/Atom"><title>Feed</title>
              <entry><title>Article</title><published>2020-05-01T00:00:00Z</published><content>Fresh body</content></entry>
            </feed>
            """,
            Md5("Article" + new DateTime(2020, 5, 1, 0, 0, 0, DateTimeKind.Utc).Ticks.ToString(CultureInfo.InvariantCulture))
        ];
        yield return
        [
            """
            {"rss":{"channel":{"title":"Feed","item":[{"title":"Article","pubDate":"1970-01-01T00:00:01Z","description":"Fresh body"}]}}}
            """,
            Md5("1Article")
        ];
        yield return
        [
            """
            {"version":"https://jsonfeed.org/version/1","title":"Feed","items":[{"id":2.0,"title":"Article","content_text":"Fresh body"}]}
            """,
            "2.0"
        ];
    }

    [Theory]
    [MemberData(nameof(LegacyCases))]
    public async Task NativeParserKeepsExistingArticleStatusAndIdentityAcrossRestart(string data, string legacyUniqueId)
    {
        var path = Path.Combine(Path.GetTempPath(), $"WinNewsWire-parser-identity-{Guid.NewGuid():N}.db");
        var native = Assert.Single(Parse(data).Items);
        Assert.NotEqual(legacyUniqueId, native.UniqueId);
        var oldId = Article.CalculatedArticleID(FeedId, legacyUniqueId);
        var arrived = new DateTime(2020, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        try
        {
            using (var database = new ArticlesDatabaseStore(path, "account"))
            {
                await Seed(database, oldId, legacyUniqueId, arrived);
                var previous = Assert.Single(await database.FetchArticlesAsync(FeedId));
                var changes = await database.UpdateAsync([native], FeedId);
                Assert.Null(changes.New);
                var updated = Assert.Single(changes.Updated!);
                Assert.Equal(oldId, updated.ArticleID);
                Assert.Equal(native.UniqueId, updated.UniqueID);
                Assert.Same(previous.Status, updated.Status);
                Assert.True(updated.Status.Read);
                Assert.True(updated.Status.Starred);
                Assert.Equal(arrived, updated.Status.DateArrived);
            }
            using (var reopened = new ArticlesDatabaseStore(path, "account"))
            {
                var repeated = await reopened.UpdateAsync([native], FeedId);
                Assert.Null(repeated.New);
                Assert.Null(repeated.Updated);
                var stored = Assert.Single(await reopened.FetchArticlesAsync(FeedId));
                Assert.Equal(oldId, stored.ArticleID);
                Assert.Equal(native.UniqueId, stored.UniqueID);
                Assert.True(stored.Status.Read);
                Assert.True(stored.Status.Starred);
                Assert.Equal(arrived, stored.Status.DateArrived);
            }
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    }

    [Fact]
    public async Task IncomingCanonicalIdTakesPriorityOverAnotherItemsLegacyAlias()
    {
        using var database = new ArticlesDatabaseStore(":memory:", "account");
        var oldId = Article.CalculatedArticleID(FeedId, "2.0");
        await Seed(database, oldId, "2.0", DateTime.UnixEpoch);
        var items = Parse("""
            {"version":"https://jsonfeed.org/version/1","title":"Feed",
             "items":[{"id":2.0,"title":"Numeric","content_text":"New"},{"id":"2.0","title":"String","content_text":"Existing"}]}
            """).Items;
        var changes = await database.UpdateAsync(items, FeedId);
        Assert.Single(changes.New!);
        var all = await database.FetchArticlesAsync(FeedId);
        Assert.Equal(2, all.Count);
        var original = Assert.Single(all, article => article.UniqueID == "2.0");
        Assert.Equal(oldId, original.ArticleID);
        Assert.True(original.Status.Starred);
        Assert.False(Assert.Single(all, article => article.UniqueID == "2").Status.Starred);
    }

    [Fact]
    public async Task ExistingNonCalculatedArticleIdRemainsStable()
    {
        using var database = new ArticlesDatabaseStore(":memory:", "account");
        await Seed(database, "original-persistent-id", "entry", DateTime.UnixEpoch);
        var item = ParsedItemFixture.Create("entry", FeedId, DateTime.UnixEpoch);
        var changes = await database.UpdateAsync([item], FeedId);
        Assert.Null(changes.New);
        Assert.Equal("original-persistent-id", Assert.Single(changes.Updated!).ArticleID);
    }

    [Fact]
    public async Task LegacyLookupIsScopedToTheFeed()
    {
        using var database = new ArticlesDatabaseStore(":memory:", "account");
        await Seed(database, "other-feed-id", "2.0", DateTime.UnixEpoch, "other-feed");
        var item = Assert.Single(Parse("""
            {"version":"https://jsonfeed.org/version/1","title":"Feed","items":[{"id":2.0,"content_text":"Body"}]}
            """).Items);
        var changes = await database.UpdateAsync([item], FeedId);
        Assert.Single(changes.New!);
        Assert.Equal("other-feed-id", Assert.Single(await database.FetchArticlesAsync("other-feed")).ArticleID);
    }

    [Fact]
    public async Task AmbiguousLegacyRowsAreNotSilentlyMerged()
    {
        using var database = new ArticlesDatabaseStore(":memory:", "account");
        await Seed(database, "first", "2.0", DateTime.UnixEpoch);
        await Seed(database, "second", "2.0", DateTime.UnixEpoch);
        var item = Assert.Single(Parse("""
            {"version":"https://jsonfeed.org/version/1","title":"Feed","items":[{"id":2.0,"content_text":"Body"}]}
            """).Items);
        await Assert.ThrowsAsync<InvalidDataException>(() => database.UpdateAsync([item], FeedId));
        var stored = await database.FetchArticlesAsync(FeedId);
        Assert.Equal(2, stored.Count);
        Assert.All(stored, article => Assert.Equal("2.0", article.UniqueID));
    }

    private static ParsedFeed Parse(string content)
        => FeedParser.Parse(new(FeedUrl, Encoding.UTF8.GetBytes(content.PadRight(128))))!;

    private static async Task Seed(ArticlesDatabaseStore database, string articleId, string uniqueId, DateTime arrived, string feedId = FeedId)
    {
        var article = new Article("account", articleId, feedId, uniqueId, "Article", "Old body", null, null,
            null, null, null, null, null, null, null, null, new ArticleStatus(articleId, true, true, arrived));
        await database.UpdateArticlesAsync([article]);
    }

    private static string Md5(string text) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
