using System.Runtime.Versioning;
using System.Text.Json;
using WinNewsWire.Core;
using WinNewsWire.Feedbin;
using WinNewsWire.ReaderAPI;
using WinNewsWire.Secrets;
using Xunit;

namespace WinNewsWire.Account.Tests;

[SupportedOSPlatform("windows")]
public class AccountManagerTests : IDisposable
{
    private readonly TestAccountManager _storage = new();

    [Fact]
    public void RefreshAvailabilityTracksTheEntireActiveAccountSession()
    {
        using var manager = new AccountManager();
        Assert.True(manager.CanRefresh);
        manager.DefaultAccount.IsActive = false;
        Assert.False(manager.CanRefresh);
        manager.DefaultAccount.IsActive = true;
        using (var refresh = CombinedRefreshProgress.Shared.BeginRefresh(manager.DefaultAccount.AccountID))
        {
            Assert.True(manager.RefreshInProgress);
            Assert.False(manager.CanRefresh);
            refresh.Report(new ProgressInfo(0, 1, 1));
            Assert.False(manager.CanRefresh);
        }
        Assert.False(manager.RefreshInProgress);
        Assert.True(manager.CanRefresh);
    }

    [Fact]
    public void LastRefreshIncludesOnlyActiveAccountsWithCompletedRefreshes()
    {
        using var manager = new AccountManager();
        Assert.Null(manager.LastRefreshCompletedDate);
        var remote = manager.CreateRemoteAccount(AccountType.FreshRSS, "Refresh fixture",
            new ReaderAPIAccountDelegate(ReaderAPIVariant.FreshRSS, "https://example.com/api/greader.php/"));
        var earlier = DateTime.UtcNow.AddDays(-1);
        manager.DefaultAccount.Settings.LastRefreshCompletedDate = earlier;
        Assert.Equal(earlier, manager.LastRefreshCompletedDate);
        var latest = DateTime.UtcNow;
        remote.Settings.LastRefreshCompletedDate = latest;
        Assert.Equal(latest, manager.LastRefreshCompletedDate);
        remote.IsActive = false;
        Assert.Equal(earlier, manager.LastRefreshCompletedDate);
        manager.DefaultAccount.IsActive = false;
        Assert.Null(manager.LastRefreshCompletedDate);
    }

    [Fact]
    public void RemoteAccountsReloadWithRealDelegatesAndStoredUsername()
    {
        var credentials = new Credentials(CredentialsType.Basic, "test-user", "test-password");
        CredentialsManager.Store(credentials, "Feedbin");
        string id;
        using (var manager = new AccountManager())
        {
            var account = manager.CreateRemoteAccount(AccountType.Feedbin, "My Feedbin", new FeedbinAccountDelegate(credentials));
            id = account.AccountID;
            var metadata = File.ReadAllText(Path.Combine(account.AccountDirectory, "Meta.json"));
            Assert.DoesNotContain(credentials.Secret, metadata);
            using var json = JsonDocument.Parse(metadata);
            Assert.Equal("test-user", json.RootElement.GetProperty("Username").GetString());
        }
        using var restored = new AccountManager();
        var reloaded = restored.Accounts.Single(a => a.AccountID == id);
        var accountDelegate = Assert.IsType<FeedbinAccountDelegate>(reloaded.Delegate);
        Assert.Equal(credentials, accountDelegate.API.Credentials);
        Assert.Equal("My Feedbin", reloaded.Name);
    }

    [Fact]
    public void FreshRssServerSurvivesRestart()
    {
        const string server = "https://example.com/custom/api/greader.php/";
        string id;
        using (var manager = new AccountManager())
            id = manager.CreateRemoteAccount(AccountType.FreshRSS, "FreshRSS",
                new ReaderAPIAccountDelegate(ReaderAPIVariant.FreshRSS, server)).AccountID;
        using var restored = new AccountManager();
        var accountDelegate = Assert.IsType<ReaderAPIAccountDelegate>(restored.Accounts.Single(a => a.AccountID == id).Delegate);
        Assert.Equal(server, accountDelegate.API.Host);
    }

    [Fact]
    public void MissingCredentialsDoNotTurnRemoteAccountsIntoLocalAccounts()
    {
        var accountDelegate = AccountDelegateFactory.Resolve(AccountType.Feedbin, "legacy-account");
        Assert.IsType<FeedbinAccountDelegate>(accountDelegate);
        Assert.True(accountDelegate.SupportsRemoteSync);
    }

    [Fact]
    public void ResolverFailuresAreNotHiddenByLocalFallback()
    {
        var previous = AccountDelegateFactory.Resolver;
        try
        {
            AccountDelegateFactory.Resolver = (_, _) => throw new InvalidOperationException("Cannot construct account.");
            Assert.Throws<InvalidOperationException>(() => AccountDelegateFactory.Resolve(AccountType.Feedbin, "test"));
        }
        finally { AccountDelegateFactory.Resolver = previous; }
    }

    [Theory]
    [InlineData((int)AccountType.CloudKit)]
    [InlineData(999)]
    public void UnsupportedAccountFilesAreKept(int accountType)
    {
        const string accountID = "Unsupported-profile";
        var directory = Path.Combine(AppConfig.AccountsDirectory, accountID);
        Directory.CreateDirectory(directory);
        var metadata = Path.Combine(directory, "Meta.json");
        var original = JsonSerializer.Serialize(new { Type = accountType, Name = "Unsupported account", IsActive = true });
        var payload = Path.Combine(directory, "preserved-history.bin");
        byte[] originalPayload = [1, 3, 5, 7];
        File.WriteAllText(metadata, original);
        File.WriteAllBytes(payload, originalPayload);
        using var manager = new AccountManager();
        Assert.Equal(original, File.ReadAllText(metadata));
        Assert.Equal(originalPayload, File.ReadAllBytes(payload));
        Assert.DoesNotContain(manager.Accounts, a => a.AccountID == accountID);
        Assert.Equal(AccountManager.DefaultAccountID, manager.DefaultAccount.AccountID);
        Assert.IsType<LocalAccountDelegate>(Assert.Single(manager.Accounts).Delegate);
    }

    public void Dispose() => _storage.Dispose();
}
