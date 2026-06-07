using System.Linq;
using Xunit;
using WinNewsWire.Parsers;

namespace WinNewsWire.Parsers.Tests;

/// <summary>Port of RSParser's <c>AtomParserTests.swift</c>.</summary>
public class AtomParserTests
{
    private static ParserData Load(string f, string url) => TestResources.Load($"{f}.atom", url);

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
            Assert.Single(a.Authors!);
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
}
