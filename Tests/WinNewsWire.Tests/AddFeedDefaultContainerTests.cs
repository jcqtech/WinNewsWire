using WinNewsWire.Account;
using WinNewsWire.AppShared.Extensions;
using WinNewsWire.Core;
using WinNewsWire.Feedly;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

[Collection("Account storage")]
public sealed class AddFeedDefaultContainerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WinNewsWire.AddFeedTests." + Guid.NewGuid().ToString("N"));
    private readonly List<Account.Account> _accounts = [];
    private readonly AppDefaults _defaults;

    public AddFeedDefaultContainerTests()
    {
        AppConfig.SetDataDirectoryOverride(_directory);
        _defaults = new(Path.Combine(_directory, "defaults.json"));
    }

    [Fact]
    public void RememberedAccountAndFolderSurviveReloadingPreferences()
    {
        var first = Create("First");
        var remembered = Create("Remembered");
        var folder = remembered.AddFolder("News");
        AddFeedDefaultContainer.SaveDefaultContainer(remembered, folder, _defaults);
        var selected = AddFeedDefaultContainer.Resolve(_accounts, new AppDefaults(Path.Combine(_directory, "defaults.json")));
        Assert.Same(remembered, selected!.Value.Account);
        Assert.Same(folder, selected.Value.Folder);
        Assert.NotSame(first, selected.Value.Account);
    }

    [Fact]
    public void InactiveOrMissingAccountsFallBackToTheFirstSortedActiveAccount()
    {
        var inactive = Create("A");
        inactive.IsActive = false;
        Create("Z");
        var expected = Create("B");
        AddFeedDefaultContainer.SaveDefaultContainer(inactive, null, _defaults);
        Assert.Same(expected, AddFeedDefaultContainer.Resolve(_accounts, _defaults)!.Value.Account);
        _defaults.SetValue(AppDefaults.Key.AddFeedAccountID, "missing");
        Assert.Same(expected, AddFeedDefaultContainer.Resolve(_accounts, _defaults)!.Value.Account);
    }

    [Fact]
    public void DeletedRememberedFolderFallsBackToItsAccountRoot()
    {
        var account = Create("Account");
        var folder = account.AddFolder("News");
        AddFeedDefaultContainer.SaveDefaultContainer(account, folder, _defaults);
        account.RemoveFolder(folder);
        var selected = AddFeedDefaultContainer.Resolve(_accounts, _defaults);
        Assert.Same(account, selected!.Value.Account);
        Assert.Null(selected.Value.Folder);
    }

    [Fact]
    public void UnrememberedDestinationsPreferTheCanonicalLocalAccountOverAlphabeticalOrder()
    {
        var earlier = Create("Aardvark");
        var local = Create("Zulu", id: AccountManager.DefaultAccountID);
        Assert.Same(local, AddFeedDefaultContainer.Resolve(_accounts, _defaults)!.Value.Account);
        _defaults.SetValue(AppDefaults.Key.AddFeedAccountID, "missing");
        Assert.Same(local, AddFeedDefaultContainer.Resolve(_accounts, _defaults)!.Value.Account);
        local.IsActive = false;
        Assert.Same(earlier, AddFeedDefaultContainer.Resolve(_accounts, _defaults)!.Value.Account);
    }

    [Fact]
    public void AccountsThatRequireFoldersUseTheFirstNaturallySortedFolder()
    {
        var account = Create("Feedly", new FeedlyAccountDelegate());
        account.AddFolder("Folder 10");
        var expected = account.AddFolder("Folder 2");
        AddFeedDefaultContainer.SaveDefaultContainer(account, null, _defaults);
        Assert.Same(expected, AddFeedDefaultContainer.Resolve(_accounts, _defaults)!.Value.Folder);
    }

    [Fact]
    public void AFolderRequiredAccountWithoutFoldersHasNoDefaultDestination()
    {
        Create("Feedly", new FeedlyAccountDelegate());
        Assert.Null(AddFeedDefaultContainer.Resolve(_accounts, _defaults));
    }

    [Fact]
    public void SavingTheAccountRootClearsThePreviousFolderPreference()
    {
        var account = Create("Account");
        AddFeedDefaultContainer.SaveDefaultContainer(account, account.AddFolder("News"), _defaults);
        AddFeedDefaultContainer.SaveDefaultContainer(account, null, _defaults);
        Assert.Null(_defaults.GetValue<string?>(AppDefaults.Key.AddFeedFolderName));
        Assert.Null(AddFeedDefaultContainer.Resolve(_accounts, _defaults)!.Value.Folder);
    }

    [Fact]
    public void NoActiveAccountsAndForeignFoldersAreHandledExplicitly()
    {
        var first = Create("First");
        var second = Create("Second");
        Assert.Throws<ArgumentException>(() => AddFeedDefaultContainer.SaveDefaultContainer(first, second.AddFolder("Foreign"), _defaults));
        first.IsActive = false;
        second.IsActive = false;
        Assert.Null(AddFeedDefaultContainer.Resolve(_accounts, _defaults));
    }

    private Account.Account Create(string name, IAccountDelegate? accountDelegate = null, string? id = null)
    {
        accountDelegate ??= new LocalAccountDelegate();
        var account = new Account.Account(id ?? Guid.NewGuid().ToString("N"), accountDelegate.Type, name, accountDelegate);
        _accounts.Add(account);
        return account;
    }

    public void Dispose()
    {
        foreach (var account in _accounts) account.Dispose();
        AppConfig.SetDataDirectoryOverride(null);
        Directory.Delete(_directory, recursive: true);
    }
}
