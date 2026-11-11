using System.Runtime.Versioning;
using WinNewsWire.Secrets;

namespace WinNewsWire.Account;

public static class AccountCredentials
{
    [SupportedOSPlatform("windows")]
    public static void StoreCredentials(this Account account, Credentials credentials)
    {
        CredentialsManager.Store(credentials, ServerFor(account.Type, account.Server));
        account.Delegate.CredentialsChanged(credentials);
        account.CredentialUsername = credentials.Username;
        AccountManager.WriteMeta(account);
    }

    [SupportedOSPlatform("windows")]
    public static Credentials? RetrieveCredentials(this Account account, CredentialsType type)
        => account.CredentialUsername is { } username
            ? Retrieve(type, account.Type, account.Server, username) : null;

    [SupportedOSPlatform("windows")]
    public static void RemoveCredentials(this Account account, CredentialsType type)
    {
        if (account.CredentialUsername is { } username)
            CredentialsManager.Remove(type, ServerFor(account.Type, account.Server), username);
    }

    internal static string ServerFor(AccountType type, string? endpoint)
        => type == AccountType.FreshRSS && !string.IsNullOrEmpty(endpoint) ? "FreshRSS:" + endpoint.TrimEnd('/') : type.ToString();

    [SupportedOSPlatform("windows")]
    internal static Credentials? Retrieve(CredentialsType credentialsType, AccountType accountType, string? endpoint, string username)
    {
        var server = ServerFor(accountType, endpoint);
        var value = CredentialsManager.Retrieve(credentialsType, server, username);
        if (value is null && server != accountType.ToString() && !CredentialsManager.ContainsScope(server, username))
            value = CredentialsManager.Retrieve(credentialsType, accountType.ToString(), username);
        return value;
    }
}
