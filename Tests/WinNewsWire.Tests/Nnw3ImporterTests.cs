using System.Text;
using WinNewsWire.AppShared.Importers;
using WinNewsWire.Parsers;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

/// <summary>
/// Verifies that the NNW3 XML-plist importer produces OPML that the <see cref="OpmlParser"/>
/// can round-trip into the same feed set.
/// </summary>
public class Nnw3ImporterTests
{
    private const string SamplePlist = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <array>
            <dict>
                <key>name</key><string>Tech</string>
                <key>isContainer</key><true/>
                <key>childrenArray</key>
                <array>
                    <dict>
                        <key>name</key><string>Daring Fireball</string>
                        <key>home</key><string>https://daringfireball.net/</string>
                        <key>rss</key><string>https://daringfireball.net/feeds/main</string>
                        <key>isContainer</key><false/>
                    </dict>
                    <dict>
                        <key>name</key><string>Inessential</string>
                        <key>home</key><string>http://inessential.com/</string>
                        <key>rss</key><string>http://inessential.com/xml/rss.xml</string>
                        <key>isContainer</key><false/>
                    </dict>
                </array>
            </dict>
            <dict>
                <key>name</key><string>Scripting News</string>
                <key>home</key><string>http://scripting.com/</string>
                <key>rss</key><string>http://scripting.com/rss.xml</string>
                <key>isContainer</key><false/>
            </dict>
        </array>
        </plist>
        """;

    [Fact]
    public async Task XmlPlistConvertsToOpmlParsableStructure()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, SamplePlist, Encoding.UTF8);
            var opml = await Nnw3Importer.ConvertToOpmlAsync(path);
            Assert.NotNull(opml);
            Assert.Contains("<outline text=\"Tech\"", opml);
            Assert.Contains("xmlUrl=\"https://daringfireball.net/feeds/main\"", opml);

            var doc = OpmlParser.Parse(new ParserData(path, Encoding.UTF8.GetBytes(opml!)));
            var children = doc.Children;
            Assert.Equal(2, children.Count);

            var folder = children[0];
            Assert.Equal("Tech", folder.Title);
            Assert.Equal(2, folder.Children.Count);
            Assert.Contains(folder.Children, c => c.FeedSpecifier?.FeedUrl == "https://daringfireball.net/feeds/main");
            Assert.Contains(folder.Children, c => c.FeedSpecifier?.FeedUrl == "http://inessential.com/xml/rss.xml");

            var topFeed = children[1];
            Assert.Equal("http://scripting.com/rss.xml", topFeed.FeedSpecifier?.FeedUrl);
            Assert.Equal("Scripting News", topFeed.FeedSpecifier?.Title);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task BinaryPlistIsRejected()
    {
        var path = Path.GetTempFileName();
        try
        {
            // bplist00 magic + garbage.
            await File.WriteAllBytesAsync(path, Encoding.ASCII.GetBytes("bplist00\x00\x01\x02\x03"));
            var opml = await Nnw3Importer.ConvertToOpmlAsync(path);
            Assert.Null(opml);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task MalformedPlistReturnsNull()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "<not plist>", Encoding.UTF8);
            Assert.Null(await Nnw3Importer.ConvertToOpmlAsync(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
