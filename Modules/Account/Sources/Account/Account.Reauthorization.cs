using System.Diagnostics;
using System.Runtime.Versioning;
using WinNewsWire.Secrets;

namespace WinNewsWire.Account;

public sealed partial class Account
{
    [SupportedOSPlatform("windows")]
    public Task ReauthorizeAsync(IAccountDelegate replacement, IReadOnlyCollection<Credentials> credentials, CancellationToken ct = default)
    {
        var username = ValidateAuthentication(Type, replacement, credentials);
        return RunOperationAsync(async token =>
        {
            if (CredentialUsername is { } previous && !previous.Equals(username, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Sign in to the same remote account. Add a separate account to use a different identity.");
            if (CredentialUsername is not null && Server is { } endpoint && replacement.Server is { } newEndpoint
                && !Uri.Equals(new Uri(endpoint, UriKind.RelativeOrAbsolute), new Uri(newEndpoint, UriKind.RelativeOrAbsolute)))
                throw new InvalidOperationException("Reauthorization cannot change the server of an identified account.");
            await Delegate.WaitForPendingWorkAsync(token);
            token.ThrowIfCancellationRequested();
            CredentialsManager.StoreMany(credentials, AccountCredentials.ServerFor(Type, replacement.Server ?? Server));
            Settings.SetAuthenticationMetadata(username, replacement.Server);
            var previousDelegate = Delegate;
            Delegate = replacement;
            if (!ReferenceEquals(previousDelegate, replacement)) await previousDelegate.DisposeAsync();
            try { AccountManager.WriteMeta(this); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Trace.TraceError($"Authentication preferences were saved, but the legacy account metadata mirror could not be updated: {error}");
            }
            AccountStructureChanged?.Invoke(this, EventArgs.Empty);
        }, ct);
    }

    internal static string ValidateAuthentication(AccountType type, IAccountDelegate replacement, IReadOnlyCollection<Credentials> credentials)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (replacement.Type != type || type is AccountType.OnMyMac or AccountType.CloudKit)
            throw new ArgumentException("Authentication must use the expected remote account type.", nameof(replacement));
        var username = replacement.CredentialUsername;
        if (string.IsNullOrWhiteSpace(username) || credentials.Count == 0 || credentials.Any(value => value.Username != username))
            throw new ArgumentException("The validated credentials must belong to the replacement account.", nameof(credentials));
        var required = type switch
        {
            AccountType.Feedly => CredentialsType.OAuthAccessToken,
            AccountType.NewsBlur => CredentialsType.NewsBlurSessionId,
            AccountType.Feedbin => CredentialsType.Basic,
            _ => CredentialsType.ReaderBasic
        };
        if (!credentials.Any(value => value.Type == required))
            throw new ArgumentException("The required account credential type is missing.", nameof(credentials));
        if (replacement.Server is { } endpoint &&
            (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0))
            throw new ArgumentException("The account server must be an HTTP or HTTPS URL without embedded credentials.", nameof(replacement));
        return username;
    }
}
