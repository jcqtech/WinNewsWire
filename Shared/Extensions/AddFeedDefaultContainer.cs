using System.Text.Json;
using WinNewsWire.Account;
using WinNewsWire.Core;

namespace WinNewsWire.AppShared.Extensions;

public static class AddFeedDefaultContainer
{
    public static (Account.Account Account, Folder? Folder)? Resolve(IEnumerable<Account.Account> accounts, AppDefaults defaults)
    {
        var active = AccountManager.SortAccounts(accounts.Where(account => account.IsActive)).ToList();
        var id = defaults.GetValue<string?>(AppDefaults.Key.AddFeedAccountID);
        var remembered = active.FirstOrDefault(account => account.AccountID == id);
        if (remembered is not null)
        {
            var folderName = defaults.GetValue<string?>(AppDefaults.Key.AddFeedFolderName);
            if (remembered.Folders.FirstOrDefault(folder => folder.NameForDisplay == folderName) is { } folder)
                return (remembered, folder);
            return SubstituteContainerIfNeeded(remembered);
        }
        return active.FirstOrDefault() is { } first ? SubstituteContainerIfNeeded(first) : null;
    }

    public static (Account.Account Account, Folder? Folder)? SubstituteContainerIfNeeded(Account.Account account)
    {
        if (!account.Type.Has(AccountBehavior.DisallowFeedInRootFolder)) return (account, null);
        var folder = account.Folders.OrderBy(folder => folder.NameForDisplay, LocalizedStandardComparer.Shared).FirstOrDefault();
        return folder is null ? null : (account, folder);
    }

    public static void SaveDefaultContainer(Account.Account account, Folder? folder, AppDefaults defaults)
    {
        if (folder is not null && (folder.AccountID != account.AccountID || !account.Folders.Contains(folder)))
            throw new ArgumentException("The selected folder no longer belongs to this account.", nameof(folder));
        defaults.SetValues(new Dictionary<string, JsonElement>
        {
            [AppDefaults.Key.AddFeedAccountID] = JsonSerializer.SerializeToElement(account.AccountID),
            [AppDefaults.Key.AddFeedFolderName] = JsonSerializer.SerializeToElement(folder?.NameForDisplay)
        });
    }
}
