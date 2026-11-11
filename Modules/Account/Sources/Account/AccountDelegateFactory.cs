using WinNewsWire.Feedly;
using WinNewsWire.ReaderAPI;
using WinNewsWire.Secrets;

namespace WinNewsWire.Account;

public static class AccountDelegateFactory
{
    public static Func<AccountType, string, IAccountDelegate>? Resolver { get; set; }

    public static IAccountDelegate Resolve(AccountType type, string accountID, string? username = null, string? server = null)
    {
        if (Resolver is not null) return Resolver(type, accountID);
        if (type == AccountType.OnMyMac) return new LocalAccountDelegate();
        if (type == AccountType.CloudKit || !Enum.IsDefined(type))
            throw new NotSupportedException($"Account type {type} is not supported on Windows.");

        var credentialsType = type switch
        {
            AccountType.NewsBlur => CredentialsType.NewsBlurSessionId,
            AccountType.Feedly => CredentialsType.OAuthAccessToken,
            AccountType.FreshRSS or AccountType.Inoreader or AccountType.BazQux or AccountType.TheOldReader => CredentialsType.ReaderBasic,
            _ => CredentialsType.Basic,
        };
        var credentials = Retrieve(credentialsType, type, username, server);
        return type switch
        {
            AccountType.Feedbin => new Feedbin.FeedbinAccountDelegate(credentials),
            AccountType.NewsBlur => new NewsBlur.NewsBlurAccountDelegate(credentials),
            AccountType.Feedly => new FeedlyAccountDelegate(credentials,
                Retrieve(CredentialsType.OAuthRefreshToken, type, username), FeedlyClientConfig.Load()),
            AccountType.FreshRSS => new ReaderAPIAccountDelegate(ReaderAPIVariant.FreshRSS, server, credentials),
            AccountType.Inoreader => new ReaderAPIAccountDelegate(ReaderAPIVariant.Inoreader, server, credentials),
            AccountType.BazQux => new ReaderAPIAccountDelegate(ReaderAPIVariant.BazQux, server, credentials),
            AccountType.TheOldReader => new ReaderAPIAccountDelegate(ReaderAPIVariant.TheOldReader, server, credentials),
            _ => throw new NotSupportedException($"Account type {type} is not supported."),
        };
    }

    private static Credentials? Retrieve(CredentialsType credentialsType, AccountType type, string? username, string? endpoint = null)
    {
        if (string.IsNullOrEmpty(username)) return null;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Reading stored credentials requires Windows.");
        return AccountCredentials.Retrieve(credentialsType, type, endpoint, username);
    }
}
