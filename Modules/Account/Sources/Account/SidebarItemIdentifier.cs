using System.Text.Json;

namespace WinNewsWire.Account;

public enum ReadFilterType { Read, None, AlwaysRead }
public enum SidebarItemKind { SmartFeed, Feed, Folder }

public sealed record SidebarItemIdentifier(SidebarItemKind Kind, string AccountID, string ID)
{
    public static SidebarItemIdentifier SmartFeed(string id) => new(SidebarItemKind.SmartFeed, "", id);
    public static SidebarItemIdentifier Feed(string accountID, string feedID) => new(SidebarItemKind.Feed, accountID, feedID);
    public static SidebarItemIdentifier Folder(string accountID, string name) => new(SidebarItemKind.Folder, accountID, name);
    public string StorageKey => JsonSerializer.Serialize(new[] { Kind.ToString(), AccountID, ID });

    public IReadOnlyDictionary<string, string> UserInfo => Kind switch
    {
        SidebarItemKind.SmartFeed => new Dictionary<string, string> { ["type"] = "smartFeed", ["id"] = ID },
        SidebarItemKind.Feed => new Dictionary<string, string> { ["type"] = "feed", ["accountID"] = AccountID, ["feedID"] = ID },
        SidebarItemKind.Folder => new Dictionary<string, string> { ["type"] = "folder", ["accountID"] = AccountID, ["folderName"] = ID },
        _ => throw new InvalidOperationException("Unknown sidebar identifier type."),
    };

    public static SidebarItemIdentifier? FromUserInfo(IReadOnlyDictionary<string, string> values)
    {
        return values.GetValueOrDefault("type") switch
        {
            "smartFeed" when values.TryGetValue("id", out var id) => SmartFeed(id),
            "feed" when values.TryGetValue("accountID", out var account)
                && (values.TryGetValue("feedID", out var feed) || values.TryGetValue("webFeedID", out feed)) => Feed(account, feed),
            "folder" when values.TryGetValue("accountID", out var account)
                && values.TryGetValue("folderName", out var folder) => Folder(account, folder),
            _ => null,
        };
    }
}
