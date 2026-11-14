using System.Text.Json;
using WinNewsWire.Articles;
using WinNewsWire.Core;
using Xunit;

namespace WinNewsWire.Account.Tests;

public sealed class DefaultAccountTests : IDisposable
{
    private readonly TestAccountManager _storage = new();

    [Fact]
    public void ARemoteOnlyProfileStillGetsTheCanonicalLocalAccount()
    {
        WriteMetadata("remote", AccountType.Feedbin, "A remote account");
        var previous = AccountDelegateFactory.Resolver;
        try
        {
            AccountDelegateFactory.Resolver = (_, _) => new RemoteDelegate();
            using var manager = new AccountManager();
            Assert.Equal("OnMyMac", manager.DefaultAccount.AccountID);
            Assert.Equal(AccountType.OnMyMac, manager.DefaultAccount.Type);
            Assert.Same(manager.DefaultAccount, manager.SortedAccounts.First());
            Assert.Equal(2, manager.Accounts.Count);
            Assert.Contains(manager.Accounts, account => account.AccountID == "remote");
        }
        finally { AccountDelegateFactory.Resolver = previous; }
    }

    [Fact]
    public void TheDefaultSortsFirstRegardlessOfItsNameOrActivation()
    {
        using var manager = new AccountManager();
        var local = manager.DefaultAccount;
        local.Name = "Zulu";
        var extra = manager.CreateLocalAccount("Aardvark");
        var remote = manager.CreateRemoteAccount(AccountType.Feedbin, "Beta", new RemoteDelegate());
        Assert.Equal(new[] { local, extra, remote }, manager.SortedAccounts);
        Assert.Equal(new[] { local, extra, remote }, manager.SortedActiveAccounts);

        local.IsActive = false;
        Assert.Same(local, manager.DefaultAccount);
        Assert.Equal(new[] { local, extra, remote }, manager.SortedAccounts);
        Assert.Equal(new[] { extra, remote }, manager.SortedActiveAccounts);
    }

    [Fact]
    public async Task TheDefaultProfileAndStatusesSurviveReopening()
    {
        string feedID;
        using (var manager = new AccountManager())
        {
            var local = manager.DefaultAccount;
            local.Name = "My local account";
            var folder = local.AddFolder("Keep");
            feedID = local.AddFeed("https://example.com/feed", "My feed", folder).FeedID;
            await local.Database.CreateStatusesIfNeededAsync(["retained"]);
            await local.MarkAsync(["retained"], ArticleStatus.Key.Starred, true);
            local.IsActive = false;
            manager.CreateLocalAccount("Another local account");
        }

        using var reopened = new AccountManager();
        var restored = reopened.DefaultAccount;
        Assert.Equal("OnMyMac", restored.AccountID);
        Assert.Equal("My local account", restored.Name);
        Assert.False(restored.IsActive);
        Assert.Equal(feedID, Assert.Single(Assert.Single(restored.Folders).Feeds).FeedID);
        Assert.Contains("retained", await restored.Database.FetchStatusArticleIDsAsync(ArticleStatus.Key.Starred, true));
        Assert.Equal(2, reopened.Accounts.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheDefaultCannotBeRemovedEvenWhenInactive(bool active)
    {
        using var manager = new AccountManager();
        var local = manager.DefaultAccount;
        local.IsActive = active;
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RemoveAccountAsync(local));
        Assert.Same(local, manager.DefaultAccount);
        Assert.Contains(local, manager.Accounts);
        Assert.False(local.IsStopping);
        Assert.True(Directory.Exists(local.AccountDirectory));
        Assert.False(Directory.Exists(Path.Combine(AppConfig.DataDirectory, "DeletedAccounts")));
        await local.Database.CreateStatusesIfNeededAsync(["still-usable"]);
    }

    [Fact]
    public async Task AdditionalLocalAccountsRemainRemovable()
    {
        using var manager = new AccountManager();
        var local = manager.DefaultAccount;
        var additional = manager.CreateLocalAccount("Temporary");
        await manager.RemoveAccountAsync(additional);
        Assert.Same(local, Assert.Single(manager.Accounts));
        Assert.False(Directory.Exists(additional.AccountDirectory));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    public void UnreadableDefaultMetadataDoesNotCreateAReplacement(string metadata)
    {
        var directory = Path.Combine(AppConfig.AccountsDirectory, "OnMyMac");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Meta.json");
        File.WriteAllText(path, metadata);
        Assert.Throws<JsonException>(() => new AccountManager());
        Assert.Equal(metadata, File.ReadAllText(path));
        Assert.Equal(directory, Assert.Single(Directory.GetDirectories(AppConfig.AccountsDirectory)));
    }

    [Theory]
    [InlineData(AccountType.Feedbin)]
    [InlineData(AccountType.CloudKit)]
    public void ARemoteProfileAtTheReservedDefaultPathIsNotReinterpretedOrOverwritten(AccountType type)
    {
        var path = WriteMetadata("OnMyMac", type, "Existing remote data");
        var metadata = File.ReadAllText(path);
        Assert.Throws<InvalidDataException>(() => new AccountManager());
        Assert.Equal(metadata, File.ReadAllText(path));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "FeedSettings.db")));
    }

    [Fact]
    public void FailedManagerConstructionReleasesTheAlreadyLoadedLocalStores()
    {
        var directory = Path.Combine(AppConfig.AccountsDirectory, "OnMyMac");
        Directory.CreateDirectory(directory);
        WriteMetadata("remote", AccountType.Feedbin, "Remote");
        var error = new InvalidOperationException("Could not construct the remote delegate.");
        var previous = AccountDelegateFactory.Resolver;
        try
        {
            AccountDelegateFactory.Resolver = (type, _) => type == AccountType.OnMyMac ? new LocalAccountDelegate() : throw error;
            Assert.Same(error, Assert.Throws<InvalidOperationException>(() => new AccountManager()));
            var databases = Directory.GetFiles(directory, "*.db");
            Assert.NotEmpty(databases);
            foreach (var path in databases)
            {
                using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                Assert.True(exclusive.Length > 0);
            }
        }
        finally { AccountDelegateFactory.Resolver = previous; }
    }

    private static string WriteMetadata(string id, AccountType type, string name)
    {
        var directory = Path.Combine(AppConfig.AccountsDirectory, id);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Meta.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { Type = (int)type, Name = name, IsActive = true }));
        return path;
    }

    private sealed class RemoteDelegate : IAccountDelegate
    {
        public AccountType Type => AccountType.Feedbin;
        public Task RefreshAllAsync(Account account, IProgress<ProgressInfo>? progress, CancellationToken ct)
            => Task.FromException(new InvalidOperationException("Default-account tests must not contact a provider."));
        public Task<Feed?> CreateFeedAsync(Account account, string urlOrSite, string? name, Folder? folder, CancellationToken ct)
            => Task.FromException<Feed?>(new InvalidOperationException("Default-account tests must not contact a provider."));
    }

    public void Dispose() => _storage.Dispose();
}
