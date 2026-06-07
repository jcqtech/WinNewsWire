namespace WinNewsWire.Account;

/// <summary>
/// Port of NetNewsWire's <c>AccountBehavior</c> enum. Encodes the capabilities
/// each account/sync service exposes so the UI can disable controls that
/// don't apply. For example, Feedly doesn't allow feeds in the root folder,
/// so the Add Feed dialog hides the "Top Level" option for Feedly accounts.
/// </summary>
[System.Flags]
public enum AccountBehavior
{
    None = 0,

    /// <summary>Account doesn't support copies of a feed in a folder to be made to the root folder.</summary>
    DisallowFeedCopyInRootFolder = 1 << 0,

    /// <summary>Account doesn't support feeds in the root folder.</summary>
    DisallowFeedInRootFolder = 1 << 1,

    /// <summary>Account doesn't support a feed being in more than one folder.</summary>
    DisallowFeedInMultipleFolders = 1 << 2,

    /// <summary>Account doesn't support folders (add/remove/rename).</summary>
    DisallowFolderManagement = 1 << 3,

    /// <summary>Account doesn't support OPML imports.</summary>
    DisallowOpmlImports = 1 << 4,

    /// <summary>Account doesn't allow renaming feeds.</summary>
    DisallowFeedRename = 1 << 5,
}

/// <summary>
/// Maps each <see cref="AccountType"/> to its canonical capabilities. Keep
/// in sync with the matching <c>behaviors</c> getters in each Swift account
/// delegate.
/// </summary>
public static class AccountBehaviors
{
    /// <summary>Returns the behaviors that apply to <paramref name="type"/>.</summary>
    public static AccountBehavior For(AccountType type) => type switch
    {
        // Local account is the most permissive — supports everything.
        AccountType.OnMyMac => AccountBehavior.None,

        // Feedbin allows feeds in the root folder and folders, but a feed can
        // only be in one folder (Feedbin's "tag" data model).
        AccountType.Feedbin => AccountBehavior.DisallowFeedInMultipleFolders,

        // NewsBlur: no root-level feeds, no multi-folder membership.
        AccountType.NewsBlur => AccountBehavior.DisallowFeedInRootFolder
                              | AccountBehavior.DisallowFeedInMultipleFolders,

        // Reader API family (FreshRSS, Inoreader, BazQux, The Old Reader):
        // root feeds allowed, but a feed lives in a single category.
        AccountType.FreshRSS or AccountType.Inoreader
            or AccountType.BazQux or AccountType.TheOldReader
            => AccountBehavior.DisallowFeedInMultipleFolders,

        // Feedly: root feeds are technically allowed, but folders are mandatory
        // for any structured organization and rename happens via the web UI.
        AccountType.Feedly => AccountBehavior.DisallowFeedInMultipleFolders
                            | AccountBehavior.DisallowFeedRename,

        // CloudKit is omitted from this Windows port — fall through to safe defaults.
        _ => AccountBehavior.None,
    };

    /// <summary>Convenience: does <paramref name="type"/> have <paramref name="flag"/>?</summary>
    public static bool Has(this AccountType type, AccountBehavior flag)
        => (For(type) & flag) == flag;
}
