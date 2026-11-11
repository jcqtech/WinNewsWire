using System.Text;
using WinNewsWire.Core;

namespace WinNewsWire.Account;

public sealed class Folder : IDisplayNameProvider, IOpmlRepresentable
{
    public string AccountID { get; }
    public string? Name { get; set; }
    public int FolderID { get; }
    public SidebarItemIdentifier SidebarItemID => SidebarItemIdentifier.Folder(AccountID, NameForDisplay);
    public ReadFilterType DefaultReadFilterType => ReadFilterType.Read;
    public string? ExternalID { get; set; }
    public List<Feed> Feeds { get; } = new();
    public string NameForDisplay => Name ?? "Untitled Folder";

    public Folder(string accountID, string? name, int folderID)
    {
        AccountID = accountID;
        Name = name;
        FolderID = folderID;
    }

    public string OpmlString(int indentLevel, bool allowCustomAttributes = false)
    {
        var title = NameForDisplay.EscapingSpecialXmlCharacters();
        var externalID = allowCustomAttributes && ExternalID is not null
            ? $" nnw_externalID=\"{ExternalID.EscapingSpecialXmlCharacters()}\"" : "";
        var indent = new string('\t', indentLevel);
        var opening = $"{indent}<outline text=\"{title}\" title=\"{title}\"{externalID}";
        if (Feeds.Count == 0) return opening + "/>\n";
        var sb = new StringBuilder(opening + ">\n");
        foreach (var feed in Feeds.OrderBy(f => f.NameForDisplay, LocalizedStandardComparer.Shared)
                     .ThenBy(f => f.Url, StringComparer.Ordinal))
            sb.Append(feed.OpmlString(indentLevel + 1, allowCustomAttributes));
        return sb.Append(indent).Append("</outline>\n").ToString();
    }
}
