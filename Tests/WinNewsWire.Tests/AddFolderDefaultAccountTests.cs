using WinNewsWire.Account;
using WinNewsWire.AppShared.Extensions;
using WinNewsWire.Core;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

[Collection("Account storage")]
public sealed class AddFolderDefaultAccountTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "folder-defaults-" + Guid.NewGuid().ToString("N"));
    private readonly List<Account.Account> _accounts = [];
    private readonly AppDefaults _defaults;
    private string PreferencesPath => Path.Combine(_directory, "defaults.json");

    public AddFolderDefaultAccountTests()
    {
        AppConfig.SetDataDirectoryOverride(_directory);
        _defaults = new AppDefaults(PreferencesPath);
    }

    [Fact]
    public void ARememberedFolderAccountSurvivesReopeningPreferences()
    {
        Create("First", AccountManager.DefaultAccountID);
        var remembered = Create("Remembered");
        AddFolderDefaultAccount.Save(remembered, _defaults);
        Assert.Same(remembered, AddFolderDefaultAccount.Resolve(_accounts, new AppDefaults(PreferencesPath)));
    }

    [Fact]
    public void MissingOrInactivePreferencesFallBackToTheDefaultLocalAccount()
    {
        var remembered = Create("Aardvark");
        var local = Create("Zulu", AccountManager.DefaultAccountID);
        AddFolderDefaultAccount.Save(remembered, _defaults);
        remembered.IsActive = false;
        Assert.Same(local, AddFolderDefaultAccount.Resolve(_accounts, _defaults));
        _defaults.SetValue(AppDefaults.Key.AddFolderAccountID, "missing");
        Assert.Same(local, AddFolderDefaultAccount.Resolve(_accounts, _defaults));
    }

    [Fact]
    public void IneligibleAccountsAreNotSavedAndAnEmptyChoiceIsExplicit()
    {
        var inactive = Create("Inactive");
        inactive.IsActive = false;
        Assert.Null(AddFolderDefaultAccount.Resolve(_accounts, _defaults));
        Assert.Throws<InvalidOperationException>(() => AddFolderDefaultAccount.Save(inactive, _defaults));
        Assert.False(_defaults.ContainsKey(AppDefaults.Key.AddFolderAccountID));
    }

    private Account.Account Create(string name, string? id = null)
    {
        var account = new Account.Account(id ?? Guid.NewGuid().ToString("N"), AccountType.OnMyMac, name, new LocalAccountDelegate());
        _accounts.Add(account);
        return account;
    }

    public void Dispose()
    {
        foreach (var account in _accounts) account.Dispose();
        AppConfig.SetDataDirectoryOverride(null);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
