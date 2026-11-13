using System.Linq;
using System.Text;
using Xunit;
using WinNewsWire.Parsers;

namespace WinNewsWire.Parsers.Tests;

/// <summary>Port of RSParser's <c>AtomParserTests.swift</c>.</summary>
public class AtomParserTests
{
    private static ParserData Load(string f, string url) => TestResources.Load($"{f}.atom", url);

    [Fact]
    public void MediaGroupResolvesUrlsAndMergesWithAtomEnclosure()
    {
        const string xml = """
            <feed xmlns="http://www.w3.org/2005/Atom" xmlns:m="http://search.yahoo.com/mrss/"
                  xml:base="https://example.com/">
            <entry><id>video</id><title>Article</title><link href="watch"/>
              <link rel="enclosure" href="media/video.mp4" type="video/mp4"/>
              <m:group xml:base="media/">
                <m:title>Wrong media title</m:title>
                <m:content url="low.mp4" width="320"/>
                <m:content url="video.mp4" medium="video" width="640" height="390" isDefault="true">
                  <title xmlns="">Wrong child title</title>
                </m:content>
                <m:thumbnail url="preview.jpg"/>
              </m:group>
            </entry></feed>
            """;
        var item = Assert.Single(AtomParser.Parse(new ParserData("https://example.com/feed",
            Encoding.UTF8.GetBytes(xml))).Items);
        Assert.Equal("Article", item.Title);
        Assert.Equal("https://example.com/watch", item.Url);
        Assert.Equal("https://example.com/media/preview.jpg", item.ImageUrl);
        var attachment = Assert.Single(item.Attachments!);
        Assert.Equal("https://example.com/media/video.mp4", attachment.Url);
        Assert.Equal("video/mp4", attachment.MimeType);
        Assert.Equal(640, attachment.Width);
        Assert.Equal(390, attachment.Height);
    }

    [Fact]
    public void XhtmlTitleWithCData()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <title>t</title><id>urn:t</id>
              <entry>
                <id>urn:t:1</id>
                <title type="xhtml"><![CDATA[Wheels within (mouse) wheels]]></title>
                <updated>2020-05-01T00:00:00Z</updated>
                <content>Body.</content>
              </entry>
            </feed>
            """;
        var feed = FeedParser.Parse(new ParserData("http://example.com/", Encoding.UTF8.GetBytes(xml)));
        Assert.NotNull(feed);
        Assert.Equal("Wheels within (mouse) wheels", Assert.Single(feed.Items).Title);
    }

    [Fact]
    public void HomePageLinks()
    {
        Assert.Equal("http://leancrew.com/all-this",
            FeedParser.Parse(Load("allthis", "http://leancrew.com/all-this"))!.HomePageUrl);
        Assert.Equal("https://www.qemu.org/",
            FeedParser.Parse(Load("qemu", "https://www.qemu.org/feed.xml"))!.HomePageUrl);
        Assert.Equal("https://yakubin.com/notes",
            FeedParser.Parse(Load("yakubin", "https://yakubin.com/notes/atom.xml"))!.HomePageUrl);
        Assert.Equal("http://4fsodonline.blogspot.com/",
            FeedParser.Parse(Load("4fsodonline", "http://4fsodonline.blogspot.com/feeds/posts/default"))!.HomePageUrl);
        Assert.Equal("https://daringfireball.net/",
            FeedParser.Parse(Load("DaringFireball", "https://daringfireball.net/feeds/main"))!.HomePageUrl);
        Assert.Equal("https://neverworkintheory.org/",
            FeedParser.Parse(Load("neverworkintheory", "https://neverworkintheory.org/atom.xml"))!.HomePageUrl);
    }

    [Fact]
    public void ArticlePermalinks()
    {
        var qemu = FeedParser.Parse(Load("qemu", "https://www.qemu.org/feed.xml"))!;
        Assert.Contains(qemu.Items,
            i => i.Title == "QEMU version 10.1.0 released" && i.Url == "https://www.qemu.org/2025/08/26/qemu-10-1-0/");

        var df = FeedParser.Parse(Load("DaringFireball", "https://daringfireball.net/feeds/main"))!;
        Assert.Contains(df.Items,
            i => i.Title == "Virgin Mobile Partners With Apple to Go iPhone-Only With $1 Service"
              && i.Url == "https://daringfireball.net/linked/2017/06/26/virgin-mobile-iphone-only");

        var nwt = FeedParser.Parse(Load("neverworkintheory", "https://neverworkintheory.org/atom.xml"))!;
        Assert.Contains(nwt.Items,
            i => i.Title == "Andreas Zeller on Creating Nasty Test Inputs"
              && i.Url == "https://neverworkintheory.org/2023/06/13/zeller-andreas.html");
    }

    [Fact]
    public void DaringFireballExternalLinks()
    {
        var df = FeedParser.Parse(Load("DaringFireball", "https://daringfireball.net/feeds/main"))!;
        Assert.Contains(df.Items,
            i => i.Title == "Kara Swisher: \u2018Susan Fowler Proved That One Person Can Make a Difference\u2019"
              && i.ExternalUrl == "https://www.recode.net/2017/6/21/15844852/uber-toxic-bro-company-culture-susan-fowler-blog-post");

        var qemu = FeedParser.Parse(Load("qemu", "https://www.qemu.org/feed.xml"))!;
        Assert.NotEmpty(qemu.Items);
        foreach (var i in qemu.Items) Assert.Null(i.ExternalUrl);
    }

    [Fact]
    public void DaringFireballItems()
    {
        var df = FeedParser.Parse(Load("DaringFireball", "https://daringfireball.net/feeds/main"))!;
        foreach (var a in df.Items)
        {
            Assert.NotNull(a.Url);
            Assert.StartsWith("tag:daringfireball.net,2017:/", a.UniqueId);
            var author = Assert.Single(a.Authors!);
            if (author.Name == "Daring Fireball Department of Commerce") Assert.Null(author.Url);
            else
            {
                Assert.Equal("John Gruber", author.Name);
                Assert.Equal("http://daringfireball.net/", author.Url);
            }
            Assert.NotNull(a.DatePublished);
            Assert.Null(a.Attachments);
            Assert.Equal("en", a.Language);
        }
    }

    [Fact]
    public void FourFsodonlineAttachments()
    {
        var f = FeedParser.Parse(Load("4fsodonline", "http://4fsodonline.blogspot.com/"))!;
        foreach (var a in f.Items)
        {
            Assert.NotNull(a.Attachments);
            Assert.NotEmpty(a.Attachments!);
            var att = a.Attachments!.First();
            Assert.StartsWith("http://www.blogger.com/video-play.mp4?", att.Url);
            Assert.Null(att.SizeInBytes);
            Assert.Equal("video/mp4", att.MimeType);
        }
    }

    [Fact]
    public void ExpertOpinionEntAttachments()
    {
        var f = FeedParser.Parse(Load("expertopinionent", "http://expertopinionent.typepad.com/my-blog/"))!;
        foreach (var a in f.Items)
        {
            if (a.Attachments is null) continue;
            Assert.Single(a.Attachments);
            var att = a.Attachments!.First();
            Assert.EndsWith(".mp3", att.Url);
            Assert.Null(att.SizeInBytes);
            Assert.Equal("audio/mpeg", att.MimeType);
        }
    }

    [Fact]
    public void RootAuthorFeedIconUrl()
    {
        var f = FeedParser.Parse(Load("root-author", "https://fvsch.com/feed.xml"))!;
        Assert.Equal("https://fvsch.com/assets/images/icon.png?v=ql0r5y", f.IconUrl);
    }

    [Fact]
    public void AuthorAtRoot()
    {
        var f = FeedParser.Parse(Load("root-author", "https://fvsch.com/feed.xml"))!;
        foreach (var a in f.Items)
        {
            var author = a.Authors?.FirstOrDefault();
            Assert.NotNull(author);
            Assert.Equal("Florens Verschelde", author!.Name);
            Assert.Null(author.Url);
            Assert.Null(author.AvatarUrl);
            Assert.Null(author.EmailAddress);
        }
    }

    [Fact]
    public void XmlBaseResolvesRelativeUrlsInXhtmlContent()
    {
        var feed = FeedParser.Parse(Load("pappacoda", "https://andrea.pappacoda.it/blog/feed.atom"))!;
        Assert.Equal("https://andrea.pappacoda.it/assets/debian-button.gif", feed.IconUrl);
        Assert.Equal("https://andrea.pappacoda.it/andrea_pappacoda.jpg", feed.FaviconUrl);
        var item = Assert.Single(feed.Items, item => item.Title == "C su Windows");
        Assert.Equal("tag:andrea.pappacoda.it,2023-06-09:c_su_windows", item.UniqueId);
        Assert.Equal("https://andrea.pappacoda.it/blog/c_su_windows/", item.Url);
        Assert.NotNull(item.ContentHtml);
        Assert.Contains("src=\"https://andrea.pappacoda.it/blog/c_su_windows/c_su_windows_visual_studio_componenti.png\"", item.ContentHtml);
        Assert.Contains("srcset=\"https://andrea.pappacoda.it/blog/c_su_windows/c_su_windows_visual_studio_componenti_light.png\"", item.ContentHtml);
        Assert.Contains("src=\"https://andrea.pappacoda.it/blog/c_su_windows/clangd_path.png\"", item.ContentHtml);
        Assert.DoesNotContain("src=\"clangd_path.png\"", item.ContentHtml);
        Assert.Contains("xmlns=\"http://www.w3.org/1999/xhtml\"", item.ContentHtml);
    }

    [Fact]
    public void NestedRelativeXmlBase()
    {
        var item = ParseEntry("nested base", """
            <link rel="alternate" href="post1/"/>
            <link rel="related" xml:base="https://elsewhere.example.net/dir/" href="related.html"/>
            <content type="html" xml:base="post1/">&lt;a href="pic.png"&gt;pic&lt;/a&gt;</content>
            """, "https://example.com/blog/feed.atom", feedBase: "https://example.com/blog/",
            feedHeader: """<title>t</title><id>urn:t</id><link rel="alternate" href="https://homepage.example.org/"/>""");
        Assert.Equal("https://example.com/blog/post1/", item.Url);
        Assert.Equal("https://elsewhere.example.net/dir/related.html", item.ExternalUrl);
        Assert.Contains("href=\"https://example.com/blog/post1/pic.png\"", item.ContentHtml);
    }

    [Fact]
    public void MimeTypeHtmlContentWithXmlBaseResolved()
    {
        var item = ParseEntry("mime type html",
            """<content type="text/html" xml:base="https://example.com/post/">&lt;img src="pic.png"&gt;</content>""",
            "https://example.com/feed.atom");
        Assert.Contains("src=\"https://example.com/post/pic.png\"", item.ContentHtml);
    }

    [Fact]
    public void RelativeXmlBaseResolvesAgainstFeedUrl()
    {
        var item = ParseEntry("release",
            """<content type="html" xml:base="/2024/04/23/qemu-9-0-0/">&lt;a href="changelog.html"&gt;changelog&lt;/a&gt;</content>""",
            "https://www.qemu.org/feed.xml", id: "/2024/04/23/qemu-9-0-0", updated: "2024-04-23T23:02:00Z");
        Assert.Equal("/2024/04/23/qemu-9-0-0", item.UniqueId);
        Assert.Contains("href=\"https://www.qemu.org/2024/04/23/qemu-9-0-0/changelog.html\"", item.ContentHtml);
    }

    [Fact]
    public void FragmentAndEmptyHrefsSurviveXmlBase()
    {
        var feed = FeedParser.Parse(Load("OneFootTsunami", "http://onefoottsunami.com/feed/atom/"))!;
        Assert.Contains(feed.Items, item => item.ContentHtml?.Contains("href=\"#fn") == true);
        Assert.DoesNotContain(feed.Items, item => item.Url?.EndsWith("wp-atom.php") == true);
    }

    [Fact]
    public void SummaryWithXmlBaseResolved()
    {
        var item = ParseEntry("summary only",
            """<summary type="html" xml:base="https://example.com/post/">&lt;img src="pic.png"&gt;</summary>""",
            "https://example.com/feed.atom");
        Assert.Contains("src=\"https://example.com/post/pic.png\"", item.ContentHtml);
    }

    [Fact]
    public void RelativeAuthorUriResolvesAgainstXmlBase()
    {
        var item = ParseEntry("relative author uri", "<author><name>Alice</name><uri>about/</uri></author>",
            "https://example.com/blog/feed.atom", feedBase: "https://example.com/blog/");
        Assert.Equal("https://example.com/blog/about/", Assert.Single(item.Authors!).Url);
    }

    [Fact]
    public void TextContentWithXmlBaseUntouched()
    {
        var item = ParseEntry("text content",
            """<content type="text" xml:base="https://example.com/post/">See src="pic.png" for details.</content>""",
            "https://example.com/feed.atom");
        Assert.Contains("src=\"pic.png\"", item.ContentHtml);
    }

    [Fact]
    public void XhtmlContentCapturedAsRawBytes()
    {
        var feed = FeedParser.Parse(Load("expertopinionent", "http://expertopinionent.typepad.com/my-blog/"))!;
        Assert.Contains(feed.Items, item => item.ContentHtml?.Contains("xmlns=\"http://www.w3.org/1999/xhtml\"") == true);
        Assert.Contains(feed.Items, item => item.ContentHtml?.Contains("<br />") == true);
    }

    [Fact]
    public void QemuAtomItemIsByteStable()
    {
        var feed = FeedParser.Parse(Load("qemu", "https://www.qemu.org/feed.xml"))!;
        var item = Assert.Single(feed.Items, item => item.Title == "QEMU version 9.0.0 released");
        Assert.Equal("/2024/04/23/qemu-9-0-0", item.UniqueId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1713913320).UtcDateTime, item.DatePublished);
        Assert.NotNull(item.ContentHtml);
        Assert.Equal(2221, item.ContentHtml.Length);
        Assert.StartsWith("<p>We\u2019d like to announce the availability of the QEMU 9.0.0 release", item.ContentHtml);
        Assert.Contains("2700+ commits from 220 authors", item.ContentHtml);
    }

    [Fact]
    public void QemuAtomSecondItemDateParses()
    {
        var feed = FeedParser.Parse(Load("qemu", "https://www.qemu.org/feed.xml"))!;
        var item = Assert.Single(feed.Items, item => item.Title == "QEMU version 9.1.0 released");
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1725404880).UtcDateTime, item.DatePublished);
    }

    [Fact]
    public void AtomSelfClosingLinkAccepted()
    {
        var item = ParseEntry("self-closing link", """<link rel="alternate" type="text/html" href="http://example.com/article"/>""");
        Assert.Equal("http://example.com/article", item.Url);
    }

    [Fact]
    public void MultipleAuthorsPerEntry()
    {
        var item = ParseEntry("Joint post", """
            <author><name>Alice</name></author>
            <author><name>Bob</name><email>bob@example.com</email></author>
            <author><name>Carol</name><uri>http://carol.example.com/</uri></author>
            """, id: "urn:example:multi:1",
            feedHeader: """<title>Multi-author</title><link href="http://example.com/"/><id>urn:example:multi</id>""");
        Assert.NotNull(item.Authors);
        Assert.Equal(3, item.Authors.Count);
        Assert.True(new HashSet<string?> { "Alice", "Bob", "Carol" }.SetEquals(item.Authors.Select(author => author.Name)));
        Assert.Contains(item.Authors, author => author.EmailAddress == "bob@example.com");
        Assert.Contains(item.Authors, author => author.Url == "http://carol.example.com/");
    }

    [Fact]
    public void ContentTypeHtml()
        => Assert.Contains("<p>Hello & welcome</p>", ParseEntry("html content", """<content type="html">&lt;p&gt;Hello &amp; welcome&lt;/p&gt;</content>""").ContentHtml);

    [Fact]
    public void ContentTypeText()
    {
        var item = ParseEntry("text content", """<content type="text">Just plain &amp; text.</content>""");
        Assert.Contains("Just plain & text.", item.ContentText ?? item.ContentHtml);
    }

    [Fact]
    public void SummaryAndContentBothPreserved()
    {
        var feed = FeedParser.Parse(Load("russcox", "http://research.swtch.com/feed.atom"))!;
        var item = Assert.Single(feed.Items, item => item.Title == "Transparent Logs for Skeptical Clients");
        Assert.Equal("How an untrusted server can publish a verifiably append-only log.", item.Summary);
        Assert.Contains("Suppose we want to maintain and publish a public, append-only log of data.", item.ContentHtml);
    }

    [Fact]
    public void SummaryOnlyPromotedToContentHtml()
    {
        var item = ParseEntry("summary only", "<summary>Just a summary.</summary>");
        Assert.Equal("Just a summary.", item.ContentHtml);
        Assert.Null(item.Summary);
    }

    [Fact]
    public void ContentOnlyLeavesSummaryNil()
    {
        var item = ParseEntry("content only", "<content>Just content.</content>");
        Assert.Equal("Just content.", item.ContentHtml);
        Assert.Null(item.Summary);
    }

    [Fact]
    public void SummaryAndContentKeptSeparate()
    {
        var item = ParseEntry("both", "<summary>The summary.</summary><content>The content.</content>");
        Assert.Equal("The content.", item.ContentHtml);
        Assert.Equal("The summary.", item.Summary);
    }

    [Fact]
    public void XhtmlSummaryCapturedAsRawBytes()
    {
        var item = ParseEntry("xhtml summary", """
            <summary type="xhtml"><div xmlns="http://www.w3.org/1999/xhtml"><p>Hello <em>world</em>.</p></div></summary>
            <content>Body.</content>
            """);
        Assert.Equal("Body.", item.ContentHtml);
        Assert.Contains("<p>Hello <em>world</em>.</p>", item.Summary);
    }

    [Fact]
    public void ContentTypeDefault()
    {
        var item = ParseEntry("default content", "<content>Default is text.</content>");
        Assert.Contains("Default is text.", item.ContentText ?? item.ContentHtml);
    }

    [Fact]
    public void ShopifyNamespaceDoesNotOverwriteTitle()
    {
        var feed = FeedParser.Parse(Load("draw-down", "https://draw-down.com/collections/magazines.atom"))!;
        Assert.Equal(25, feed.Items.Count);
        Assert.DoesNotContain(feed.Items, item => item.Title == "Default Title");
        var item = Assert.Single(feed.Items, item => item.UniqueId == "https://draw-down.com/products/9225111929086");
        Assert.Equal("Revue Faire \u2014 no. 56 (Reading Until I Sleep: Manuel Raeder)", item.Title);
    }

    private static ParsedItem ParseEntry(string title, string content, string url = "http://example.com/",
        string? feedBase = null, string id = "urn:t:1", string updated = "2020-05-01T00:00:00Z",
        string feedHeader = "<title>t</title><id>urn:t</id>")
    {
        var baseAttribute = feedBase is null ? "" : $" xml:base=\"{feedBase}\"";
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom"{baseAttribute}>
              {feedHeader}
              <entry>
                <id>{id}</id>
                <title>{title}</title>
                <updated>{updated}</updated>
                {content}
              </entry>
            </feed>
            """;
        var feed = FeedParser.Parse(new ParserData(url, Encoding.UTF8.GetBytes(xml)));
        Assert.NotNull(feed);
        return Assert.Single(feed.Items);
    }
}
