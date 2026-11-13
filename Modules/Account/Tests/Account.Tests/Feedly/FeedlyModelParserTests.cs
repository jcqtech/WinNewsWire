using WinNewsWire.Feedly;
using Xunit;

namespace WinNewsWire.Account.Tests.Feedly;

public sealed class FeedlyTextSanitizationTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(" ", " ")]
    [InlineData("text", "text")]
    [InlineData("<div style=\"direction:rtl;text-align:right\">", "<div style=\"direction:rtl;text-align:right\">")]
    [InlineData("</div>", "</div>")]
    [InlineData("<div style=\"direction:rtl;text-align:right\">text", "<div style=\"direction:rtl;text-align:right\">text")]
    [InlineData("text</div>", "text</div>")]
    [InlineData("<div style=\"direction:rtl;text-align:right\"></div>", "")]
    [InlineData("<DIV style=\"direction:rtl;text-align:right\"></div>", "<DIV style=\"direction:rtl;text-align:right\"></div>")]
    [InlineData("<div style=\"direction:rtl;text-align:right\"></DIV>", "<div style=\"direction:rtl;text-align:right\"></DIV>")]
    [InlineData("<div style=\"direction:rtl;text-align:right\">text</div>", "text")]
    public void RTLSanitization(string? target, string? expected)
        => Assert.Equal(expected, FeedlyRTLTextSanitizer.Sanitize(target));
}

public sealed class FeedlyCollectionParserTests
{
    [Fact]
    public void Parsing()
    {
        var collection = new FeedlyCollection("test/collection/1", "Test Collection", []);
        var parser = new FeedlyCollectionParser(collection);
        Assert.Equal(collection.Label, parser.FolderName);
        Assert.Equal(collection.Id, parser.ExternalID);
    }

    [Fact]
    public void Sanitization()
    {
        var parser = new FeedlyCollectionParser(new("test/collection/1",
            "<div style=\"direction:rtl;text-align:right\">Test Collection</div>", []));
        Assert.Equal("Test Collection", parser.FolderName);
        Assert.Equal("test/collection/1", parser.ExternalID);
    }
}

public sealed class FeedlyFeedParserTests
{
    [Fact]
    public void Parsing() => AssertFeed("Test Feed");

    [Fact]
    public void Sanitization() => AssertFeed("<div style=\"direction:rtl;text-align:right\">Test Feed</div>");

    private static void AssertFeed(string title)
    {
        var parser = new FeedlyFeedParser(new("feed/tests://nnw/feed.xml", title, null, "tests://nnw/feed/1"));
        Assert.Equal("Test Feed", parser.Title);
        Assert.Equal("tests://nnw/feed/1", parser.HomePageUrl);
        Assert.Equal("tests://nnw/feed.xml", parser.Url);
        Assert.Equal("feed/tests://nnw/feed.xml", parser.FeedID);
    }
}

public sealed class FeedlyEntryParserTests
{
    [Theory]
    [InlineData("https://example.test/image.jpg", "https://example.test/image.jpg")]
    [InlineData("none", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void VisualImageIsParsedExceptForFeedlysMissingImageSentinel(string? url, string? expected)
    {
        var entry = System.Text.Json.JsonSerializer.Deserialize<FeedlyEntry>(
            System.Text.Json.JsonSerializer.Serialize(new { id = "entry", crawled = 0, origin = new { streamId = "feed/1" }, visual = new { url } }))!;
        Assert.Equal(expected, new FeedlyEntryParser(entry).ParsedItemRepresentation!.ImageUrl);
    }

    private static readonly DateTime ReferenceDate = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static long Milliseconds(DateTime date) => new DateTimeOffset(date).ToUnixTimeMilliseconds();
    private static FeedlyEntry Entry => new("tests/feeds/1/entries/1", "Test Entry 1", null, null, null,
        Milliseconds(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc)), null, null, null, null, false, null, null, null);

    [Fact]
    public void Parsing()
    {
        var entry = Entry with
        {
            Content = new("Test Content", "ltr"), Summary = new("Test Summary", "ltr"), Author = "Bob Alice",
            Recrawled = Milliseconds(ReferenceDate), Origin = new("Test Feed", "tests://feeds/1", null),
            Canonical = [new("tests://feeds/1/entries/1", "text/html")],
            Tags = [new("tests/tags/1", "Tag 1"), new("tests/tags/2", "Tag 2")],
        };
        var parser = new FeedlyEntryParser(entry);
        Assert.Equal(entry.Id, parser.ID);
        Assert.Equal(entry.Origin.StreamId, parser.FeedUrl);
        Assert.Equal(entry.Canonical[0].Href, parser.ExternalUrl);
        Assert.Equal(entry.Title, parser.Title);
        Assert.Equal(entry.Content.Content, parser.ContentHtml);
        Assert.Equal(entry.Summary.Content, parser.Summary);
        Assert.Equal(DateTime.MinValue, parser.DatePublished);
        Assert.Equal(ReferenceDate, parser.DateModified);
        var item = Assert.IsType<Parsers.ParsedItem>(parser.ParsedItemRepresentation);
        Assert.Equal(entry.Id, item.SyncServiceId);
        Assert.Equal(entry.Id, item.UniqueId);
        Assert.Equal(entry.Origin.StreamId, item.FeedUrl);
        Assert.Equal(entry.Title, item.Title);
        Assert.Equal(entry.Content.Content, item.ContentHtml);
        Assert.Null(item.ContentText);
        Assert.Equal(entry.Summary.Content, item.Summary);
        Assert.Equal(parser.DatePublished, item.DatePublished);
        Assert.Equal(parser.DateModified, item.DateModified);
        Assert.True(item.Tags!.SetEquals(["Tag 1", "Tag 2"]));
        Assert.Equal("Bob Alice", Assert.Single(item.Authors!).Name);
    }

    [Fact]
    public void Sanitization()
    {
        var entry = Entry with
        {
            Title = "<div style=\"direction:rtl;text-align:right\">Test Entry 1</div>",
            Summary = new("<div style=\"direction:rtl;text-align:right\">Test Summary</div>", "rtl"),
            Content = new("<div style=\"direction:rtl;text-align:right\">Test Content</div>", "rtl"),
        };
        var parser = new FeedlyEntryParser(entry);
        Assert.Equal("Test Entry 1", parser.Title);
        Assert.Equal("Test Summary", parser.Summary);
        Assert.Equal(entry.Content.Content, parser.ContentHtml);
    }

    [Fact]
    public void LocatesCanonicalExternalURL()
    {
        var parser = new FeedlyEntryParser(Entry with
        {
            Canonical = [new("tests://feeds/1/entries/1", "text/html")],
            Alternate = [new("tests://feeds/1/entries/alternate/1", "text/html")],
        });
        Assert.Equal("tests://feeds/1/entries/1", parser.ExternalUrl);
    }

    [Fact]
    public void LocatesAlternateExternalURL()
    {
        var parser = new FeedlyEntryParser(Entry with
        {
            Canonical = [new("tests://feeds/1/entries/1", "text/json")],
            Alternate = [new("tests://feeds/1/entries/alternate/1", null)],
        });
        Assert.Equal("tests://feeds/1/entries/alternate/1", parser.ExternalUrl);
    }

    [Fact]
    public void ContentPreferredToSummary()
    {
        var parser = new FeedlyEntryParser(Entry with { Content = new("Test Content", "ltr"), Summary = new("Test Summary", "ltr") });
        Assert.Equal("Test Content", parser.ContentHtml);
    }

    [Fact]
    public void PublishedDateUsedWhenNotAfterCrawlDate()
    {
        var parser = new FeedlyEntryParser(Entry with
        {
            Crawled = Milliseconds(ReferenceDate.AddSeconds(1_000_000)), Published = Milliseconds(ReferenceDate.AddSeconds(500_000)),
        });
        Assert.Equal(ReferenceDate.AddSeconds(500_000), parser.DatePublished);
    }

    [Fact]
    public void FutureDatedPublishedDateFallsBackToCrawlDate()
    {
        var parser = new FeedlyEntryParser(Entry with
        {
            Crawled = Milliseconds(ReferenceDate.AddSeconds(1_000_000)), Published = Milliseconds(ReferenceDate.AddSeconds(2_000_000)),
        });
        Assert.Equal(ReferenceDate.AddSeconds(1_000_000), parser.DatePublished);
    }

    [Fact]
    public void SummaryUsedAsContentWhenContentMissing()
    {
        var parser = new FeedlyEntryParser(Entry with { Summary = new("Test Summary", "ltr") });
        Assert.Equal("Test Summary", parser.ContentHtml);
    }
}
