using WinNewsWire.Core;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace WinNewsWire.Account.Tests;

internal sealed class TestAccountManager : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WinNewsWire.AccountTests." + Guid.NewGuid().ToString("N"));
    private readonly List<Account> _accounts = [];

    public TestAccountManager() => AppConfig.SetDataDirectoryOverride(_directory);

    public Account CreateAccount(string? id = null, IAccountDelegate? accountDelegate = null)
    {
        accountDelegate ??= new LocalAccountDelegate();
        var account = new Account(id ?? Guid.NewGuid().ToString("N"), accountDelegate.Type, "Test", accountDelegate);
        _accounts.Add(account);
        return account;
    }

    public Account Reopen(Account account, IAccountDelegate? replacement = null)
    {
        replacement ??= account.Type == AccountType.OnMyMac ? new LocalAccountDelegate()
            : throw new ArgumentException("Supply a fresh delegate with an isolated transport when reopening a remote test account.", nameof(replacement));
        if (replacement.Type != account.Type || ReferenceEquals(replacement, account.Delegate))
            throw new ArgumentException("Reopening requires a new delegate of the same account type.", nameof(replacement));
        account.Dispose();
        _accounts.Remove(account);
        return CreateAccount(account.AccountID, replacement);
    }

    public void Dispose()
    {
        foreach (var account in _accounts) account.Dispose();
        AppConfig.SetDataDirectoryOverride(null);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
