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
        AccountType.OnMyMac or AccountType.OneDrive => AccountBehavior.None,

        AccountType.Feedbin => AccountBehavior.DisallowFeedCopyInRootFolder,
        AccountType.NewsBlur => AccountBehavior.None,

        // Reader API family (FreshRSS, Inoreader, BazQux, The Old Reader):
        // root feeds allowed, but a feed lives in a single category.
        AccountType.FreshRSS => AccountBehavior.DisallowFeedInMultipleFolders
                              | AccountBehavior.DisallowFeedInRootFolder,
        AccountType.Inoreader or AccountType.BazQux or AccountType.TheOldReader
            => AccountBehavior.DisallowFeedInMultipleFolders,

        AccountType.Feedly => AccountBehavior.DisallowFeedInRootFolder,

        // CloudKit is omitted from this Windows port — fall through to safe defaults.
        _ => AccountBehavior.None,
    };

    /// <summary>Convenience: does <paramref name="type"/> have <paramref name="flag"/>?</summary>
    public static bool Has(this AccountType type, AccountBehavior flag)
        => (For(type) & flag) == flag;
}
