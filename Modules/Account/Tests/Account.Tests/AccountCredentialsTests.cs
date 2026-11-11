using System.Runtime.Versioning;
using WinNewsWire.Feedbin;
using WinNewsWire.Secrets;
using Xunit;

namespace WinNewsWire.Account.Tests;

/// <summary>Account/Tests/AccountTests/AccountCredentialsTest.swift, using isolated Windows DPAPI storage.</summary>
[SupportedOSPlatform("windows")]
public class AccountCredentialsTests : IDisposable
{
    private readonly TestAccountManager _manager = new();

    [Fact]
    public void CreateRetrieveDelete()
    {
        var account = _manager.CreateAccount(accountDelegate: new FeedbinAccountDelegate());
        account.RemoveCredentials(CredentialsType.Basic);
        account.StoreCredentials(new Credentials(CredentialsType.Basic, "maurice", "hardpasswd"));
        var credentials = account.RetrieveCredentials(CredentialsType.Basic);
        Assert.NotNull(credentials);
        Assert.Equal(CredentialsType.Basic, credentials.Type);
        Assert.Equal("maurice", credentials.Username);
        Assert.Equal("hardpasswd", credentials.Secret);

        account.StoreCredentials(new Credentials(CredentialsType.Basic, "maurice", "easypasswd"));
        credentials = account.RetrieveCredentials(CredentialsType.Basic);
        Assert.NotNull(credentials);
        Assert.Equal(CredentialsType.Basic, credentials.Type);
        Assert.Equal("maurice", credentials.Username);
        Assert.Equal("easypasswd", credentials.Secret);

        account.RemoveCredentials(CredentialsType.Basic);
        Assert.Null(account.RetrieveCredentials(CredentialsType.Basic));
    }

    [Fact]
    public void UpdatingTheUsernameUpdatesThePersistentLookupKey()
    {
        var account = _manager.CreateAccount(accountDelegate: new FeedbinAccountDelegate(
            new Credentials(CredentialsType.Basic, "old-user", "old-test-password")));
        var updated = new Credentials(CredentialsType.Basic, "new-user", "new-test-password");
        account.StoreCredentials(updated);
        Assert.Equal(updated, account.RetrieveCredentials(CredentialsType.Basic));
        Assert.Contains("\"Username\": \"new-user\"", File.ReadAllText(Path.Combine(account.AccountDirectory, "Meta.json")));
    }

    public void Dispose() => _manager.Dispose();
}
