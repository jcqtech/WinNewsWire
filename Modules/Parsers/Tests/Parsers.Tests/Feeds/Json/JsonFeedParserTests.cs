using System.Linq;
using System.Text;
using Xunit;
using WinNewsWire.Parsers;

namespace WinNewsWire.Parsers.Tests;

/// <summary>Port of RSParser's <c>JSONFeedParserTests.swift</c>.</summary>
public class JsonFeedParserTests
{
    private static ParserData Load(string f, string url) => TestResources.Load($"{f}.json", url);

    [Fact]
    public void AttachmentsWithoutMimeTypeArePreserved()
    {
        var json = """
            {"version":"https://jsonfeed.org/version/1.1","title":"Media","items":[
              {"id":"1","content_text":"Episode","attachments":[
                {"url":"https://example.com/episode","title":"Listen","size_in_bytes":123},
                {"url":"","mime_type":"audio/mpeg"},
                {"mime_type":"audio/mpeg"}]}]}
            """;
        var feed = JsonFeedParser.Parse(new ParserData("https://example.com/feed", Encoding.UTF8.GetBytes(json)))!;
        var attachment = Assert.Single(Assert.Single(feed.Items).Attachments!);
        Assert.Equal("https://example.com/episode", attachment.Url);
        Assert.Null(attachment.MimeType);
        Assert.Equal("Listen", attachment.Title);
        Assert.Equal(123, attachment.SizeInBytes);
    }

    [Fact]
    public void DaringFireballIconUrls()
    {
        var f = FeedParser.Parse(Load("DaringFireball", "http://daringfireball.net/"))!;
        Assert.Equal("https://daringfireball.net/graphics/favicon-64.png", f.FaviconUrl);
        Assert.Equal("https://daringfireball.net/graphics/apple-touch-icon.png", f.IconUrl);
    }

    [Fact]
    public void AllThisItemCount()
    {
        var f = FeedParser.Parse(Load("allthis", "http://leancrew.com/allthis/"))!;
        Assert.Equal(12, f.Items.Count);
    }

    [Fact]
    public void CurtContainsTwitterQuitter()
    {
        var f = FeedParser.Parse(Load("curt", "http://curtclifton.net/"))!;
        Assert.Equal(26, f.Items.Count);
        Assert.Contains(f.Items,
            i => i.Title == "Twitter Quitter"
              && i.ContentHtml!.StartsWith("<p>I&#8217;ve decided to close my Twitter account. William Van Hecke <a href=\"https://tinyletter.com/fet/letters/microcosmographia-xlxi-reasons-to-stay-on-twitter\">makes a convincing case</a>"));
    }

    [Fact]
    public void PxlnvItemCount() =>
        Assert.Equal(20, FeedParser.Parse(Load("pxlnv", "http://pxlnv.com/"))!.Items.Count);

    [Fact]
    public void RoseItemCount() =>
        Assert.Equal(84, FeedParser.Parse(Load("rose", "http://www.rosemaryorchard.com/"))!.Items.Count);

    [Fact]
    public void ThreeSixtyLanguage()
    {
        var f = FeedParser.Parse(Load("3960", "http://journal.3960.org/"))!;
        Assert.Equal(20, f.Items.Count);
        Assert.Equal("de-DE", f.Language);
        foreach (var i in f.Items) Assert.Equal("de-DE", i.Language);
    }

    [Fact]
    public void AuthorsResolution()
    {
        var f = FeedParser.Parse(Load("authors", "https://example.com/"))!;
        Assert.Equal(4, f.Items.Count);

        Assert.NotNull(f.Authors);
        Assert.Equal(2, f.Authors!.Count);
        var rootNames = f.Authors.Select(a => a.Name).OrderBy(s => s).ToArray();
        Assert.Equal(new[] { "Root Author 1", "Root Author 2" }, rootNames);
        Assert.True(new HashSet<ParsedAuthor>
        {
            new("Root Author 1", null, null, null), new("Root Author 2", null, null, null)
        }.SetEquals(f.Authors));

        var noAuthors = f.Items.Single(i => i.UniqueId == "Item without authors");
        Assert.Null(noAuthors.Authors);

        var legacy = f.Items.Single(i => i.UniqueId == "Item with legacy author");
        Assert.Equal(new[] { "Legacy Item Author" },
            legacy.Authors!.Select(a => a.Name).ToArray());
        Assert.True(new HashSet<ParsedAuthor> { new("Legacy Item Author", null, null, null) }.SetEquals(legacy.Authors!));

        string[] ItemNames(ParsedItem i) =>
            i.Authors!.Select(a => a.Name!).OrderBy(s => s).ToArray();

        var modern = f.Items.Single(i => i.UniqueId == "Item with modern authors");
        Assert.Equal(new[] { "Item Author 1", "Item Author 2" }, ItemNames(modern));

        var both = f.Items.Single(i => i.UniqueId == "Item with both");
        Assert.Equal(new[] { "Item Author 1", "Item Author 2" }, ItemNames(both));
        var itemAuthors = new HashSet<ParsedAuthor> { new("Item Author 1", null, null, null), new("Item Author 2", null, null, null) };
        Assert.True(itemAuthors.SetEquals(modern.Authors!));
        Assert.True(itemAuthors.SetEquals(both.Authors!));
    }

    [Theory]
    [InlineData("2.0", "2")]
    [InlineData("20e0", "20")]
    [InlineData("1.5", "1.5")]
    public void NumericIdsAreCoercedLikeFoundation(string number, string expected)
    {
        var feed = Parse($$"""{"version":"https://jsonfeed.org/version/1","title":"t","items":[{"id":{{number}},"content_text":"body"}]}""");
        Assert.Equal(expected, Assert.Single(feed.Items).UniqueId);
    }

    [Fact]
    public void EmptyArraysRemainDistinctFromMissingProperties()
    {
        var feed = Parse("""
            {"version":"https://jsonfeed.org/version/1","title":"t","home_page_url":"","authors":[],
             "items":[{"id":"1","content_text":"body","authors":[],"tags":[],"attachments":[]}]}
            """);
        Assert.Equal("", feed.HomePageUrl);
        Assert.NotNull(feed.Authors);
        Assert.Empty(feed.Authors);
        var item = Assert.Single(feed.Items);
        Assert.NotNull(item.Authors);
        Assert.NotNull(item.Tags);
        Assert.NotNull(item.Attachments);
        Assert.Empty(item.Authors);
        Assert.Empty(item.Tags);
        Assert.Empty(item.Attachments);
    }

    [Fact]
    public void MalformedItemArrayReportsTheNativeError()
    {
        var error = Assert.Throws<FeedParserException>(() => Parse("""
            {"version":"https://jsonfeed.org/version/1","title":"t","items":[42]}
            """));
        Assert.Equal(FeedParserErrorKind.JsonFeedItemsNotFound, error.Kind);
    }

    [Fact]
    public void OptionalMalformedArraysDoNotCrashParsing()
    {
        var feed = Parse("""
            {"version":"https://jsonfeed.org/version/1","title":"t","authors":[42],"author":{"name":"Legacy"},
             "hubs":[null],"items":[{"id":"1","content_text":"body","tags":[42],"attachments":[1]}]}
            """);
        Assert.Equal("Legacy", Assert.Single(feed.Authors!).Name);
        Assert.Null(feed.Hubs);
        var item = Assert.Single(feed.Items);
        Assert.Null(item.Tags);
        Assert.Null(item.Attachments);
    }

    [Fact]
    public void FractionalAttachmentSizeIsIgnoredRatherThanThrowing()
    {
        var feed = Parse("""
            {"version":"https://jsonfeed.org/version/1","title":"t",
             "items":[{"id":"1","content_text":"body","attachments":[{"url":"https://example.com/audio","mime_type":"audio/mpeg","size_in_bytes":1.5}]}]}
            """);
        Assert.Null(Assert.Single(Assert.Single(feed.Items).Attachments!).SizeInBytes);
    }

    [Fact]
    public void AttachmentDurationUsesTheNative64BitIntegerRange()
    {
        var feed = Parse("""
            {"version":"https://jsonfeed.org/version/1","title":"t",
             "items":[{"id":"1","content_text":"body","attachments":[{"url":"https://example.com/audio","mime_type":"audio/mpeg","duration_in_seconds":4294967296}]}]}
            """);
        Assert.Equal(4294967296L, Assert.Single(Assert.Single(feed.Items).Attachments!).DurationInSeconds);
    }

    private static ParsedFeed Parse(string json) => JsonFeedParser.Parse(new("https://example.com/feed", Encoding.UTF8.GetBytes(json)))!;
}
