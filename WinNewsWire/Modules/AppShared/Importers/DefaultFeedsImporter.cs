using System.Reflection;
using System.Xml.Linq;
using WinNewsWire.Account;

namespace WinNewsWire.AppShared.Importers;

/// <summary>Port of <c>DefaultFeedsImporter</c>. Reads the embedded DefaultFeeds.opml into an account.</summary>
public static class DefaultFeedsImporter
{
    public static void ImportDefaultFeeds(Account.Account account)
    {
        var asm = typeof(DefaultFeedsImporter).Assembly;
        using var s = asm.GetManifestResourceStream(asm.GetName().Name + ".Resources.DefaultFeeds.opml");
        if (s is null) return;
        var doc = XDocument.Load(s);
        foreach (var outline in doc.Descendants("outline"))
        {
            var xmlUrl = (string?)outline.Attribute("xmlUrl");
            var title = (string?)outline.Attribute("title") ?? (string?)outline.Attribute("text");
            if (string.IsNullOrEmpty(xmlUrl)) continue;
            account.AddFeed(xmlUrl, title);
        }
    }
}
