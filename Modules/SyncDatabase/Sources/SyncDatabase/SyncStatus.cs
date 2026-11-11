using WinNewsWire.Articles;

namespace WinNewsWire.SyncDatabase;

/// <summary>Port of <c>SyncStatus</c>.</summary>
public sealed record SyncStatus(string ArticleID, SyncStatus.SyncKey Key, bool Flag, bool Selected = false)
{
    public enum SyncKey { Read, Starred, Deleted, New }

    public static SyncKey FromArticleStatusKey(ArticleStatus.Key key)
        => key == ArticleStatus.Key.Read ? SyncKey.Read : SyncKey.Starred;
}
