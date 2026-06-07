using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinNewsWire.Account;
using WinNewsWire.AppRuntime;
using WinNewsWire.Secrets;

namespace WinNewsWire.Dialogs;

/// <summary>Port of <c>AccountsAddViewController</c> / <c>AccountsAddWindowController</c>.
/// Validates credentials with the remote service, stores them in DPAPI, creates the
/// account, and wires the matching delegate.</summary>
public static class AddAccountDialog
{
    private sealed record Option(string Label, AccountType Type);

    private static readonly List<Option> _options = new()
    {
        new("On My PC (Local)", AccountType.OnMyMac),
        new("Feedbin", AccountType.Feedbin),
        new("NewsBlur", AccountType.NewsBlur),
        new("FreshRSS", AccountType.FreshRSS),
        new("Inoreader", AccountType.Inoreader),
        new("BazQux", AccountType.BazQux),
        new("The Old Reader", AccountType.TheOldReader),
        new("Feedly", AccountType.Feedly),
    };

    public static async Task<Account.Account?> ShowAsync(XamlRoot root)
    {
        var typeCombo = new ComboBox { MinWidth = 280, PlaceholderText = "Account type" };
        foreach (var o in _options) typeCombo.Items.Add(new ComboBoxItem { Content = o.Label, Tag = o.Type });
        typeCombo.SelectedIndex = 0;

        var nameBox = new TextBox { PlaceholderText = "Display Name", MinWidth = 280 };
        var userBox = new TextBox { PlaceholderText = "Email / Username", MinWidth = 280 };
        var passBox = new PasswordBox { PlaceholderText = "Password / API Key", MinWidth = 280 };
        var hostBox = new TextBox { PlaceholderText = "Server URL (e.g. https://yourfreshrss/api/greader.php)", MinWidth = 280 };
        var status = new TextBlock { Foreground = (Microsoft.UI.Xaml.Media.Brush)App.Current.Resources["TextFillColorSecondaryBrush"] };

        void UpdateVisibility()
        {
            var t = (AccountType)((ComboBoxItem)typeCombo.SelectedItem).Tag;
            userBox.Visibility = t == AccountType.OnMyMac || t == AccountType.Feedly
                ? Visibility.Collapsed : Visibility.Visible;
            passBox.Visibility = t == AccountType.OnMyMac ? Visibility.Collapsed : Visibility.Visible;
            hostBox.Visibility = (t == AccountType.FreshRSS) ? Visibility.Visible : Visibility.Collapsed;
            if (t == AccountType.Feedly)
            {
                passBox.PlaceholderText = "(Leave blank to sign in via browser, or paste refresh token)";
                status.Text = "Feedly requires FEEDLY_CLIENT_ID/FEEDLY_CLIENT_SECRET env vars "
                             + "or %LocalAppData%/WinNewsWire/feedly-client.json for interactive sign-in.";
            }
            else
            {
                passBox.PlaceholderText = "Password / API Key";
                if (status.Text.StartsWith("Feedly requires", StringComparison.Ordinal)) status.Text = "";
            }
        }
        typeCombo.SelectionChanged += (_, _) => UpdateVisibility();

        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = "Account Type:" });
        stack.Children.Add(typeCombo);
        stack.Children.Add(new TextBlock { Text = "Display Name:" });
        stack.Children.Add(nameBox);
        stack.Children.Add(userBox);
        stack.Children.Add(passBox);
        stack.Children.Add(hostBox);
        stack.Children.Add(status);
        UpdateVisibility();

        var dialog = new ContentDialog
        {
            Title = "Add Account",
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
            Content = stack,
        };

        while (true)
        {
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return null;

            var option = (AccountType)((ComboBoxItem)typeCombo.SelectedItem).Tag;
            var displayName = string.IsNullOrWhiteSpace(nameBox.Text) ? option.ToString() : nameBox.Text.Trim();

            try
            {
                var (ok, delegateObj, creds, credType, server) = await ValidateAndBuildAsync(option, userBox.Text.Trim(), passBox.Password, hostBox.Text.Trim());
                if (!ok)
                {
                    status.Text = "Could not sign in. Check your credentials and try again.";
                    continue;
                }
                var account = AppService.Shared.Accounts.CreateRemoteAccount(option, displayName, delegateObj);
                if (creds is not null && credType.HasValue)
                {
                    try { CredentialsManager.Store(creds, server ?? option.ToString()); } catch { }
                }
                return account;
            }
            catch (Exception ex)
            {
                status.Text = $"Error: {ex.Message}";
            }
        }
    }

    private static async Task<(bool ok, IAccountDelegate? del, Credentials? creds, CredentialsType? credType, string? server)> ValidateAndBuildAsync(
        AccountType type, string username, string password, string host)
    {
        switch (type)
        {
            case AccountType.OnMyMac:
                return (true, new LocalAccountDelegate(), null, null, null);

            case AccountType.Feedbin:
            {
                var creds = new Credentials(CredentialsType.Basic, username, password);
                var del = new Feedbin.FeedbinAccountDelegate(creds);
                var ok = await del.ValidateCredentialsAsync(CancellationToken.None);
                return (ok, del, creds, CredentialsType.Basic, "Feedbin");
            }
            case AccountType.NewsBlur:
            {
                var caller = new NewsBlur.NewsBlurAPICaller();
                var sessionId = await caller.LoginAsync(username, password);
                if (sessionId is null) return (false, null, null, null, null);
                var creds = new Credentials(CredentialsType.NewsBlurSessionId, username, sessionId);
                return (true, new NewsBlur.NewsBlurAccountDelegate(creds), creds, CredentialsType.NewsBlurSessionId, "NewsBlur");
            }
            case AccountType.FreshRSS or AccountType.Inoreader or AccountType.BazQux or AccountType.TheOldReader:
            {
                var variant = type switch
                {
                    AccountType.Inoreader => ReaderAPI.ReaderAPIVariant.Inoreader,
                    AccountType.BazQux => ReaderAPI.ReaderAPIVariant.BazQux,
                    AccountType.TheOldReader => ReaderAPI.ReaderAPIVariant.TheOldReader,
                    _ => ReaderAPI.ReaderAPIVariant.FreshRSS,
                };
                var caller = new ReaderAPI.ReaderAPICaller(variant, string.IsNullOrEmpty(host) ? null : host);
                var token = await caller.LoginAsync(username, password);
                if (token is null) return (false, null, null, null, null);
                var creds = new Credentials(CredentialsType.ReaderBasic, username, password);
                var del = new ReaderAPI.ReaderAPIAccountDelegate(variant, string.IsNullOrEmpty(host) ? null : host, creds, token);
                return (true, del, creds, CredentialsType.ReaderBasic, type.ToString());
            }
            case AccountType.Feedly:
            {
                var config = Feedly.FeedlyClientConfig.Load();
                // If a client config is available, prefer the interactive OAuth code-grant flow —
                // this opens the browser, waits for the loopback callback, and exchanges the auth
                // code for access+refresh tokens. If no client config, fall back to accepting a
                // pre-existing refresh token pasted into the password field.
                if (config is not null && string.IsNullOrEmpty(password))
                {
                    Feedly.FeedlyOAuthAccessTokenResponse token;
                    try { token = await Feedly.FeedlyBrowserAuth.SignInAsync(config, http: null, CancellationToken.None); }
                    catch { return (false, null, null, null, null); }
                    var access = new Credentials(CredentialsType.OAuthAccessToken, token.Id, token.AccessToken);
                    var refresh = token.RefreshToken is { Length: > 0 } rt
                        ? new Credentials(CredentialsType.OAuthRefreshToken, token.Id, rt) : null;
                    var del = new Feedly.FeedlyAccountDelegate(access, refresh, config);
                    // Store the refresh token separately so AppService can rehydrate it later.
                    if (refresh is not null) { try { CredentialsManager.Store(refresh, "Feedly"); } catch { } }
                    return (true, del, access, CredentialsType.OAuthAccessToken, "Feedly");
                }
                // Manual-refresh-token path (unchanged from pre-OAuth stub days).
                var manualRefresh = new Credentials(CredentialsType.OAuthRefreshToken, username, password);
                try { CredentialsManager.Store(manualRefresh, "Feedly"); } catch { }
                return (true, new Feedly.FeedlyAccountDelegate(accessToken: null, refreshToken: manualRefresh, config: config),
                        manualRefresh, CredentialsType.OAuthRefreshToken, "Feedly");
            }

            default:
                return (false, null, null, null, null);
        }
    }
}
