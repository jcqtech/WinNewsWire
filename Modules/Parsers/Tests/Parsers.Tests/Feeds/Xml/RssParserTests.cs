using System.Linq;
using Xunit;
using WinNewsWire.Parsers;

namespace WinNewsWire.Parsers.Tests;

/// <summary>Port of RSParser's <c>RSSParserTests.swift</c>. Performance tests are omitted.</summary>
public class RssParserTests
{
    private static ParserData Load(string f, string ext, string url) => TestResources.Load($"{f}.{ext}", url);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FeedArtworkIsNotAnArticleEnclosure(bool atom)
    {
        var xml = atom
            ? """
              <feed xmlns="http://www.w3.org/2005/Atom"><entry><id>1</id>
              <link rel="enclosure" href="https://example.com/feed.jpg" type="image/jpeg"/>
              <link rel="enclosure" href="https://example.com/photo.jpg" type="image/jpeg"/>
              </entry><icon>https://example.com/icon.png</icon><logo>https://example.com/feed.jpg</logo></feed>
              """
            : """
              <rss><channel><item><guid>1</guid>
              <enclosure url="https://example.com/feed.jpg" type="image/jpeg"/>
              <enclosure url="https://example.com/photo.jpg" type="image/jpeg"/>
              </item><image><url>https://example.com/feed.jpg</url></image></channel></rss>
              """;
        var data = new ParserData("https://example.com/feed", System.Text.Encoding.UTF8.GetBytes(xml));
        var item = Assert.Single((atom ? AtomParser.Parse(data) : RssParser.Parse(data)).Items);
        Assert.Equal("https://example.com/photo.jpg", Assert.Single(item.Attachments!).Url);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SingleGravatarCanUseAnAuthorsDifferentUsernameWithoutGuessingForMultipleAuthors(bool multipleAuthors)
    {
        var xml = """
            <rss xmlns:m="http://search.yahoo.com/mrss/" xmlns:dc="http://purl.org/dc/elements/1.1/">
            <channel><item><guid>1</guid><dc:creator>Hal Mueller</dc:creator>
            <m:content url="https://2.gravatar.com/avatar/hal" medium="image"><m:title>halmueller</m:title></m:content>
            """ + (multipleAuthors ? "<dc:creator>Alice</dc:creator>" : "") + "</item></channel></rss>";
        var item = Assert.Single(RssParser.Parse(new ParserData("https://example.com/feed",
            System.Text.Encoding.UTF8.GetBytes(xml))).Items);
        Assert.Equal(multipleAuthors ? null : "https://2.gravatar.com/avatar/hal",
            item.Authors!.Single(a => a.Name == "Hal Mueller").AvatarUrl);
        if (multipleAuthors) Assert.Single(item.Attachments!);
        else Assert.Null(item.Attachments);
    }

    [Theory]
    [InlineData("gravatar.com", true)]
    [InlineData("2.gravatar.com", true)]
    [InlineData("gravatar.com.example.net", false)]
    [InlineData("example.com", false)]
    public void GravatarMediaIsUsedAsAuthorAvatarInsteadOfAttachment(string host, bool matches)
    {
        var url = $"https://{host}/avatar/alice";
        var xml = $"""
            <rss xmlns:m="http://search.yahoo.com/mrss/" xmlns:dc="http://purl.org/dc/elements/1.1/">
            <channel><item><guid>1</guid><title>Article</title><dc:creator>Alice</dc:creator>
            <m:content url="{url}" medium="image"><m:title>aLiCe</m:title></m:content>
            </item></channel></rss>
            """;
        var item = Assert.Single(RssParser.Parse(new ParserData("https://example.com/feed",
            System.Text.Encoding.UTF8.GetBytes(xml))).Items);
        Assert.Equal("Article", item.Title);
        Assert.Equal(matches ? url : null, Assert.Single(item.Authors!).AvatarUrl);
        if (matches) Assert.Null(item.Attachments);
        else Assert.Equal(url, Assert.Single(item.Attachments!).Url);
    }

    [Fact]
    public void DuplicateMediaMetadataMergesInPlaceWithoutReordering()
    {
        const string xml = """
            <rss xmlns:m="http://search.yahoo.com/mrss/"><channel><item><guid>1</guid>
            <enclosure url="https://example.com/z.mp3" type="audio/mpeg"/>
            <enclosure url="https://example.com/a.mp3" type="audio/mpeg"/>
            <m:content url="https://example.com/z.mp3" duration="180" medium="audio"/>
            </item></channel></rss>
            """;
        var attachments = Assert.Single(RssParser.Parse(new ParserData("https://example.com/feed",
            System.Text.Encoding.UTF8.GetBytes(xml))).Items).Attachments!;
        Assert.Equal(new[] { "https://example.com/z.mp3", "https://example.com/a.mp3" }, attachments.Select(a => a.Url));
        Assert.Equal(180, attachments[0].DurationInSeconds);
        Assert.Equal("audio/mpeg", attachments[0].MimeType);
    }

    [Theory]
    [InlineData(false, "https://example.com/podcast.jpg")]
    [InlineData(true, "https://example.com/preview.jpg")]
    public void ITunesImageIsFallbackForMediaThumbnail(bool mediaThumbnail, string expected)
    {
        var xml = """
            <rss xmlns:i="http://www.itunes.com/dtds/podcast-1.0.dtd" xmlns:m="http://search.yahoo.com/mrss/">
            <channel><item><guid>1</guid><i:image href="https://example.com/podcast.jpg"/>
            """ + (mediaThumbnail ? "<m:thumbnail url='https://example.com/preview.jpg'/>" : "")
            + "</item></channel></rss>";
        Assert.Equal(expected, Assert.Single(RssParser.Parse(new ParserData("https://example.com/feed",
            System.Text.Encoding.UTF8.GetBytes(xml))).Items).ImageUrl);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void MediaDescriptionIsOnlyUsedAsFallback(bool atom, bool html, bool existingBody)
    {
        var description = $"<m:description type='{(html ? "html" : "plain")}'>&lt;b&gt;Media description&lt;/b&gt;</m:description>";
        var body = existingBody ? atom ? "<content>Existing body</content>" : "<description>Existing body</description>" : "";
        var xml = atom
            ? $"<feed xmlns='http://www.w3.org/2005/Atom' xmlns:m='http://search.yahoo.com/mrss/'><entry><id>1</id><m:group>{description}</m:group>{body}</entry></feed>"
            : $"<rss xmlns:m='http://search.yahoo.com/mrss/'><channel><item><guid>1</guid><m:group>{description}</m:group>{body}</item></channel></rss>";
        var data = new ParserData("https://example.com/feed", System.Text.Encoding.UTF8.GetBytes(xml));
        var item = Assert.Single((atom ? AtomParser.Parse(data) : RssParser.Parse(data)).Items);
        Assert.Equal(existingBody ? "Existing body" : html ? "<b>Media description</b>" : null, item.ContentHtml);
        Assert.Equal(!existingBody && !html ? "<b>Media description</b>" : null, item.ContentText);
    }

    [Fact]
    public void MediaContentMergesMetadataWithEnclosure()
    {
        var feed = FeedParser.Parse(Load("atp", "rss", "http://atp.fm/"))!;
        var item = feed.Items.Single(item => item.Title == "311: Mutually Assured Destruction");
        var attachment = Assert.Single(item.Attachments!);
        Assert.Equal("http://traffic.libsyn.com/atpfm/atp311.mp3", attachment.Url);
        Assert.Equal("audio/mpeg", attachment.MimeType);
        Assert.Equal(54919661, attachment.SizeInBytes);
        Assert.Equal(6849, attachment.DurationInSeconds);
        Assert.Equal(ParsedAttachment.AttachmentMedium.Audio, attachment.Medium);
    }

    [Theory]
    [InlineData("http://search.yahoo.com/mrss/")]
    [InlineData("http://www.rssboard.org/media-rss")]
    public void MediaGroupKeepsDefaultRenditionAndDoesNotOverwriteItemTitle(string mediaNamespace)
    {
        var xml = $"""
            <rss xmlns:m="{mediaNamespace}"><channel><item><guid>1</guid><title>Article</title>
            <m:group><m:title>Media title</m:title>
            <m:content url="https://example.com/low.mp4" medium="video" width="640" height="360">
              <title>Wrong title</title><link>https://wrong.example/</link>
            </m:content>
            <m:content URL="https://example.com/high.mp4" TYPE="video/mp4" medium="Video"
              width="1920" height="1080" duration="125.9" isDefault="TRUE"/>
            <m:thumbnail url="https://example.com/preview.jpg"/>
            </m:group></item></channel></rss>
            """;
        var item = Assert.Single(RssParser.Parse(new ParserData("https://example.com/feed",
            System.Text.Encoding.UTF8.GetBytes(xml))).Items);
        Assert.Equal("Article", item.Title);
        Assert.Null(item.ExternalUrl);
        Assert.Equal("https://example.com/preview.jpg", item.ImageUrl);
        var attachment = Assert.Single(item.Attachments!);
        Assert.Equal("https://example.com/high.mp4", attachment.Url);
        Assert.Equal("video/mp4", attachment.MimeType);
        Assert.Equal(ParsedAttachment.AttachmentMedium.Video, attachment.Medium);
        Assert.Equal(1920, attachment.Width);
        Assert.Equal(1080, attachment.Height);
        Assert.Equal(125, attachment.DurationInSeconds);
    }

    [Theory]
    [InlineData("1:02:03.9", 3723L)]
    [InlineData("59:30", 3570L)]
    [InlineData("120.9", 120L)]
    [InlineData("NaN", null)]
    [InlineData("Infinity", null)]
    [InlineData("-1", null)]
    [InlineData("0", null)]
    [InlineData("1::3", null)]
    [InlineData("1:2:3:4", null)]
    [InlineData("9999999999999999999999999", null)]
    public void ITunesDurationAppliesOnlyToAudioAndVideo(string duration, long? expected)
    {
        var xml = $"""
            <rss xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd"><channel><item>
            <guid>1</guid><itunes:duration>{duration}</itunes:duration>
            <enclosure url="https://example.com/a.mp3" type="audio/mpeg"/>
            <enclosure url="https://example.com/picture.jpg" type="image/jpeg"/>
            </item></channel></rss>
            """;
        var item = Assert.Single(RssParser.Parse(new ParserData("https://example.com/feed",
            System.Text.Encoding.UTF8.GetBytes(xml))).Items);
        Assert.Equal(expected, item.Attachments!.Single(a => a.MimeType == "audio/mpeg").DurationInSeconds);
        Assert.Null(item.Attachments!.Single(a => a.MimeType == "image/jpeg").DurationInSeconds);
    }

    [Fact]
    public void IgnoredElementsAndSupersededDescriptionsDoNotAllocateStrings()
    {
        const int length = 256 * 1024;
        var ignored = new string('x', length);
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            "<rss xmlns:content='http://purl.org/rss/1.0/modules/content/'><channel><item>" +
            "<guid isPermaLink='false'>item</guid><title>Title</title>" +
            "<content:encoded><![CDATA[<p>Body</p>]]></content:encoded>" +
            "<description><![CDATA[" + ignored + "]]></description>" +
            "<unused><![CDATA[" + ignored + "]]></unused></item></channel></rss>");
        var data = new ParserData("https://example.com/feed", bytes);
        RssParser.Parse(data);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var feed = RssParser.Parse(data);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var item = Assert.Single(feed.Items);
        Assert.Equal("item", item.UniqueId);
        Assert.Equal("Title", item.Title);
        Assert.Equal("<p>Body</p>", item.ContentHtml);
        Assert.True(allocated < length + 64 * 1024, $"Ignored XML text allocated {allocated} bytes.");
    }

    [Fact]
    public void NatashaTheRobotItemCount()
    {
        var feed = FeedParser.Parse(Load("natasha", "xml", "https://www.natashatherobot.com/"))!;
        Assert.Equal(10, feed.Items.Count);
    }

    [Fact]
    public void TheOmniShowAttachments()
    {
        var feed = FeedParser.Parse(Load("theomnishow", "rss", "https://theomnishow.omnigroup.com/"))!;
        foreach (var a in feed.Items)
        {
            Assert.NotNull(a.Attachments);
            Assert.Single(a.Attachments!);
            var att = a.Attachments!.First();
            Assert.NotNull(att.MimeType);
            Assert.NotNull(att.SizeInBytes);
            Assert.Contains("cloudfront", att.Url);
            Assert.True(att.SizeInBytes >= 22275279);
            Assert.Equal("audio/mpeg", att.MimeType);
        }
    }

    [Fact]
    public void TheOmniShowUniqueIds()
    {
        var feed = FeedParser.Parse(Load("theomnishow", "rss", "https://theomnishow.omnigroup.com/"))!;
        foreach (var a in feed.Items)
            Assert.StartsWith("https://theomnishow.omnigroup.com/episode/", a.UniqueId);
    }

    [Fact]
    public void MacworldUniqueIdsAreMd5()
    {
        var feed = FeedParser.Parse(Load("macworld", "rss", "https://www.macworld.com/"))!;
        foreach (var a in feed.Items)
            Assert.Equal(32, a.UniqueId.Length);
    }

    [Fact]
    public void MacworldAuthorsHaveNamesOnly()
    {
        var feed = FeedParser.Parse(Load("macworld", "rss", "https://www.macworld.com/"))!;
        foreach (var a in feed.Items)
        {
            var author = a.Authors!.First();
            Assert.Null(author.EmailAddress);
            Assert.Null(author.Url);
            Assert.NotNull(author.Name);
        }
    }

    [Fact]
    public void EmptyContentEncodedIsIgnored()
    {
        var feed = FeedParser.Parse(Load("atp", "rss", "http://atp.fm/"))!;
        foreach (var a in feed.Items)
            Assert.NotNull(a.ContentHtml);
    }

    [Fact]
    public void LivemintGuidsNotPermalinks()
    {
        var feed = FeedParser.Parse(Load("livemint", "xml", "https://www.livemint.com/rss/news"))!;
        foreach (var a in feed.Items)
            Assert.Null(a.Url);
    }

    [Fact]
    public void AktualityTitlesPresent()
    {
        var feed = FeedParser.Parse(Load("aktuality", "rss", "https://www.aktuality.sk/"))!;
        foreach (var a in feed.Items) Assert.NotNull(a.Title);
    }

    [Fact]
    public void MantonFeedLanguage()
    {
        var feed = FeedParser.Parse(Load("manton", "rss", "http://manton.org/"))!;
        Assert.Equal("en-US", feed.Language);
    }

    [Fact]
    public void KatieFloydIconUrl()
    {
        var feed = FeedParser.Parse(Load("KatieFloyd", "rss", "http://katiefloyd.com/"))!;
        Assert.Equal("https://static.feedpress.it/logo/katiefloyd.png", feed.IconUrl);
    }

    [Fact]
    public void AktualityIconNotSetFromItemImages()
    {
        var feed = FeedParser.Parse(Load("aktuality", "rss", "https://www.aktuality.sk/"))!;
        Assert.Null(feed.IconUrl);
    }

    [Fact]
    public void MedscapeExternalUrls()
    {
        var feed = FeedParser.Parse(Load("medscape", "rss", "https://www.medscape.com/cx/rssfeeds/2674.xml"))!;
        foreach (var a in feed.Items) Assert.NotNull(a.ExternalUrl);
    }

    [Fact]
    public void CloudblogAuthorTitleNotUsedAsItemTitle()
    {
        var feed = FeedParser.Parse(Load("cloudblog", "rss", "https://cloudblog.withgoogle.com/"))!;
        foreach (var a in feed.Items)
        {
            Assert.NotEqual("Product Manager, Office of the CTO", a.Title);
            Assert.NotEqual("Developer Programs Engineer", a.Title);
            Assert.NotEqual("Product Director", a.Title);
        }
    }

    [Fact]
    public void MonkeydomGuidsThatArentPermalinks()
    {
        var feed = FeedParser.Parse(Load("monkeydom", "rss", "https://coding.monkeydom.de/"))!;
        foreach (var a in feed.Items)
        {
            Assert.Null(a.Url);
            Assert.NotNull(a.UniqueId);
        }
    }

    // Port of Swift `testMarkdown1`. RSS feeds carrying source:markdown should
    // populate ParsedItem.Markdown for every item.
    [Fact]
    public void Markdown1()
    {
        var feed = FeedParser.Parse(Load("markdown1", "rss",
            "https://wordland.social/scripting/237777565/rss.xml"))!;
        Assert.NotEmpty(feed.Items);
        foreach (var a in feed.Items)
            Assert.NotNull(a.Markdown);
    }

    // Port of Swift `testMarkdown2`. Same as Markdown1 with a different feed.
    [Fact]
    public void Markdown2()
    {
        var feed = FeedParser.Parse(Load("markdown2", "rss",
            "https://wordland.social/scripting/246529703/rss.xml"))!;
        Assert.NotEmpty(feed.Items);
        foreach (var a in feed.Items)
            Assert.NotNull(a.Markdown);
    }

    [Fact]
    public void FeedWithGb2312Encoding()
    {
        var feed = FeedParser.Parse(Load("kc0011", "rss", "http://kc0011.net/"))!;
        Assert.NotEmpty(feed.Items);
        Assert.All(feed.Items, item => Assert.NotNull(item.ContentHtml));
    }

    [Fact]
    public void CDataBodyPreservesHtmlNamedEntities()
    {
        var feed = FeedParser.Parse(Load("KatieFloyd", "rss", "http://katiefloyd.com/"))!;
        Assert.Contains(feed.Items, item => item.ContentHtml?.Contains("&nbsp;") == true);
        foreach (var item in feed.Items)
            if (item.ContentHtml is { } html) Assert.DoesNotContain("\u00A0", html);
    }

    [Fact]
    public void CDataBodyPreservesNumericEntities()
    {
        var feed = FeedParser.Parse(Load("DaringFireball", "rss", "http://daringfireball.net/"))!;
        Assert.Contains(feed.Items, item => item.ContentHtml?.Contains("&#8217;") == true);
    }

    [Fact]
    public void Gb2312FeedDecodesChineseCorrectly()
    {
        var feed = FeedParser.Parse(Load("kc0011", "rss", "http://kc0011.net/"))!;
        Assert.Equal("\u6295\u8D44\u8D44\u8BAF\u7F51\u4EA4\u6613\u5728\u7EBF--\u6D41\u901A\u7EAA\u5FF5\u5E01\u6700\u65B020\u7BC7\u8BBA\u575B\u4E3B\u9898-\u5168\u6587", feed.Title);
    }

    [Fact]
    public void CjkItemTitlesPreserveMultiByteUtf8()
    {
        var feed = FeedParser.Parse(Load("kc0011", "rss", "http://kc0011.net/"))!;
        var sawChinese = false;
        foreach (var item in feed.Items)
        {
            if (item.Title is not { } title) continue;
            sawChinese |= title.Any(c => c is >= '\u4E00' and <= '\u9FFF');
            Assert.DoesNotContain("\uFFFD", title);
        }
        Assert.True(sawChinese);
    }

    [Fact]
    public void RdfFeedParsesAsRss()
    {
        var feed = FeedParser.Parse(Load("bio", "rdf", "http://biorxiv.org/"))!;
        Assert.Equal("bioRxiv Subject Collection: Plant Biology", feed.Title);
        Assert.Equal("http://biorxiv.org", feed.HomePageUrl);
        Assert.Equal(30, feed.Items.Count);
        foreach (var item in feed.Items)
        {
            Assert.NotNull(item.Title);
            Assert.NotNull(item.Url);
            Assert.StartsWith("http://biorxiv.org/cgi/content/", item.UniqueId);
        }
    }

    [Fact]
    public void MacworldCalculatedUniqueIdsArePinnedMd5s()
    {
        var feed = FeedParser.Parse(Load("macworld", "rss", "https://www.macworld.com/"))!;
        var expected = new Dictionary<string, string>
        {
            ["Black Friday Exclusive: Save Over 40% On The Nix Mini Color Sensor - Deal Alert"] = "7ee68b71282bbf0f9a203f5c362e048a",
            ["The iMac Pro might be the first ARM Mac, but it won\u2019t be the last"] = "d2d30947445157657f425924d96fd139",
            ["Cyber Monday Steal: Get 120+ Hours of iOS 11 Dev Training for $15"] = "1da507ba09dfd933733211e182f95db7"
        };
        foreach (var (title, id) in expected)
            Assert.Equal(id, Assert.Single(feed.Items, item => item.Title == title).UniqueId);
    }

    [Fact]
    public void DaringFireballRssItemIsByteStable()
    {
        var feed = FeedParser.Parse(Load("DaringFireball", "rss", "http://daringfireball.net/"))!;
        var item = Assert.Single(feed.Items, item => item.Title == "The Dangerous All Writs Act Precedent in the Apple Encryption Case");
        Assert.Equal("tag:daringfireball.net,2016:/linked//6.32163", item.UniqueId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1456420033).UtcDateTime, item.DatePublished);
        Assert.NotNull(item.ContentHtml);
        Assert.Equal(1360, item.ContentHtml.Length);
        Assert.StartsWith("<p>Amy Davidson, writing for The New Yorker:</p>", item.ContentHtml);
        Assert.Contains("\u201Call writs necessary", item.ContentHtml);
    }

    [Fact]
    public void EmarleyAuthorIsNameOnly()
    {
        var feed = FeedParser.Parse(Load("EMarley", "rss", "https://emarley.com/"))!;
        var author = Assert.Single(feed.Items.First().Authors!);
        Assert.Equal("Liz Marley", author.Name);
        Assert.Null(author.EmailAddress);
        Assert.Null(author.Url);
    }

    [Fact]
    public void NonStandardSlashDateFormatParsesCorrectly()
    {
        var feed = FeedParser.Parse(Load("kc0011", "rss", "http://kc0011.net/"))!;
        var start = new DateTime(2020, 1, 10, 0, 0, 0, DateTimeKind.Utc);
        Assert.Contains(feed.Items, item => item.DatePublished >= start && item.DatePublished < start.AddDays(1));
    }

    [Fact]
    public void ShopifyNamespaceDoesNotOverwriteTitle()
    {
        var feed = FeedParser.Parse(Load("shopify-namespace", "rss", "https://example.com/feed"))!;
        var item = Assert.Single(feed.Items);
        Assert.Equal("Real Product Title", item.Title);
        Assert.Equal("<p>Body HTML</p>", item.ContentHtml);
        Assert.Equal("Jane Maker", Assert.Single(item.Authors!).Name);
    }

    [Fact]
    public void SlashdotTextInputDoesNotOverwriteFeedTitle()
    {
        var feed = FeedParser.Parse(Load("slashdot", "rss", "https://rss.slashdot.org/Slashdot/slashdotMain"))!;
        Assert.Equal("Slashdot", feed.Title);
        Assert.Equal("https://slashdot.org/", feed.HomePageUrl);
        Assert.Equal(15, feed.Items.Count);
    }

    [Fact]
    public void TextInputInsideChannelIsIgnored()
    {
        var feed = FeedParser.Parse(Load("textinput-rss2", "rss", "https://example.com/feed"))!;
        Assert.Equal("Test Feed", feed.Title);
        Assert.Equal("https://example.com", feed.HomePageUrl);
        var item = Assert.Single(feed.Items);
        Assert.Equal("Real Article Title", item.Title);
        Assert.Equal("https://example.com/articles/1", item.Url);
    }
}
