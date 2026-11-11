using WinNewsWire.Account;
using WinNewsWire.Core;

namespace WinNewsWire.AppShared.Extensions;

public static class AddFolderDefaultAccount
{
    public static Account.Account? Resolve(IEnumerable<Account.Account> accounts, AppDefaults defaults)
    {
        var eligible = AccountManager.SortAccounts(accounts.Where(account => account.IsActive
            && !account.Type.Has(AccountBehavior.DisallowFolderManagement))).ToList();
        var remembered = defaults.GetValue<string?>(AppDefaults.Key.AddFolderAccountID);
        return eligible.FirstOrDefault(account => account.AccountID == remembered) ?? eligible.FirstOrDefault();
    }

    public static void Save(Account.Account account, AppDefaults defaults)
    {
        if (!account.IsActive || account.Type.Has(AccountBehavior.DisallowFolderManagement))
            throw new InvalidOperationException("The selected account cannot create folders.");
        defaults.SetValue(AppDefaults.Key.AddFolderAccountID, account.AccountID);
    }
}
