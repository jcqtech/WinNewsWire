using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinNewsWire.Account;
using WinNewsWire.AppRuntime;
using WinNewsWire.Secrets;
using WinNewsWire.AppWindows;

namespace WinNewsWire.Dialogs.AccountSheets;

internal static class AccountSheetHelpers
{
    public static TextBox LabeledTextBox(StackPanel host, string label, string placeholder)
    {
        host.Children.Add(new TextBlock { Text = label });
        var tb = new TextBox { PlaceholderText = placeholder, MinWidth = 320 };
        host.Children.Add(tb);
        return tb;
    }

    public static PasswordBox LabeledPasswordBox(StackPanel host, string label, string placeholder)
    {
        host.Children.Add(new TextBlock { Text = label });
        var pb = new PasswordBox { PlaceholderText = placeholder, MinWidth = 320 };
        host.Children.Add(pb);
        return pb;
    }

    public static TextBlock AddStatus(StackPanel host)
    {
        var tb = new TextBlock
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)App.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        };
        host.Children.Add(tb);
        return tb;
    }

    public static bool IsSetupError(Exception error) => error is IOException or UnauthorizedAccessException
        or CryptographicException or JsonException or HttpRequestException or CredentialsException
        or InvalidOperationException or ArgumentException or OperationCanceledException;

    public static void Prefill(Account.Account? existing, TextBox name, TextBox? username = null, TextBox? server = null)
    {
        if (existing is null) return;
        name.Text = existing.Name;
        name.IsReadOnly = true;
        if (username is not null)
        {
            username.Text = existing.CredentialUsername ?? "";
            username.IsReadOnly = existing.CredentialUsername is not null;
        }
        if (server is not null)
        {
            server.Text = existing.Server ?? "";
            server.IsReadOnly = existing.CredentialUsername is not null && existing.Server is not null;
        }
    }

    public static async Task<Account.Account?> CommitAsync(Account.Account? existing, string name,
        IAccountDelegate accountDelegate, IReadOnlyCollection<Credentials> credentials, TextBlock status)
    {
        try
        {
            if (existing is null)
                return AppService.Shared.Accounts.CreateAuthenticatedAccount(accountDelegate.Type, name, accountDelegate, credentials);
            await existing.ReauthorizeAsync(accountDelegate, credentials);
            return existing;
        }
        catch (Exception ex) when (IsSetupError(ex))
        {
            status.Text = "Could not update this account: " + ex.Message;
            return null;
        }
        finally
        {
            if (!AppService.Shared.Accounts.Accounts.Any(account => ReferenceEquals(account.Delegate, accountDelegate)))
                await accountDelegate.DisposeAsync();
        }
    }
}

public static class AddLocalAccountSheet
{
    public static async Task<Account.Account?> ShowAsync(XamlRoot root)
    {
        var stack = new StackPanel { Spacing = 8 };
        var name = AccountSheetHelpers.LabeledTextBox(stack, "Display Name:", "e.g. Personal");
        var dialog = new ContentDialog
        {
            Title = "On My PC",
            Content = stack,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };
        WindowThemeHelper.Apply(dialog);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        var displayName = string.IsNullOrWhiteSpace(name.Text) ? "On My PC" : name.Text.Trim();
        return AppService.Shared.Accounts.CreateRemoteAccount(AccountType.OnMyMac, displayName, new LocalAccountDelegate());
    }
}

public static class AddFeedbinAccountSheet
{
    public static async Task<Account.Account?> ShowAsync(XamlRoot root, Account.Account? existing = null)
    {
        var stack = new StackPanel { Spacing = 8 };
        var name = AccountSheetHelpers.LabeledTextBox(stack, "Display Name:", "Feedbin");
        var user = AccountSheetHelpers.LabeledTextBox(stack, "Email:", "you@example.com");
        var pass = AccountSheetHelpers.LabeledPasswordBox(stack, "Password:", "Feedbin password");
        var status = AccountSheetHelpers.AddStatus(stack);
        AccountSheetHelpers.Prefill(existing, name, user);

        var dialog = new ContentDialog
        {
            Title = existing is null ? "Feedbin" : "Reconnect Feedbin",
            Content = stack,
            PrimaryButtonText = existing is null ? "Add" : "Reconnect",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };
        while (true)
        {
            WindowThemeHelper.Apply(dialog);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
            var creds = new Credentials(CredentialsType.Basic, user.Text.Trim(), pass.Password);
            var del = new Feedbin.FeedbinAccountDelegate(creds);
            bool ok;
            try { ok = await del.ValidateCredentialsAsync(CancellationToken.None); }
            catch (Exception ex) when (AccountSheetHelpers.IsSetupError(ex)) { del.Dispose(); status.Text = ex.Message; continue; }
            if (!ok) { del.Dispose(); status.Text = "Could not sign in. Check your credentials and try again."; continue; }
            var displayName = string.IsNullOrWhiteSpace(name.Text) ? "Feedbin" : name.Text.Trim();
            if (await AccountSheetHelpers.CommitAsync(existing, displayName, del, [creds], status) is { } account) return account;
        }
    }
}

public static class AddNewsBlurAccountSheet
{
    public static async Task<Account.Account?> ShowAsync(XamlRoot root, Account.Account? existing = null)
    {
        var stack = new StackPanel { Spacing = 8 };
        var name = AccountSheetHelpers.LabeledTextBox(stack, "Display Name:", "NewsBlur");
        var user = AccountSheetHelpers.LabeledTextBox(stack, "Username:", "");
        var pass = AccountSheetHelpers.LabeledPasswordBox(stack, "Password:", "");
        var status = AccountSheetHelpers.AddStatus(stack);
        AccountSheetHelpers.Prefill(existing, name, user);

        var dialog = new ContentDialog
        {
            Title = existing is null ? "NewsBlur" : "Reconnect NewsBlur",
            Content = stack,
            PrimaryButtonText = existing is null ? "Add" : "Reconnect",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };
        while (true)
        {
            WindowThemeHelper.Apply(dialog);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
            using var caller = new NewsBlur.NewsBlurAPICaller();
            string? sessionId;
            try { sessionId = await caller.LoginAsync(user.Text.Trim(), pass.Password); }
            catch (Exception ex) when (AccountSheetHelpers.IsSetupError(ex)) { status.Text = ex.Message; continue; }
            if (sessionId is null) { status.Text = "Could not sign in. Check your credentials and try again."; continue; }
            var creds = new Credentials(CredentialsType.NewsBlurSessionId, user.Text.Trim(), sessionId);
            var del = new NewsBlur.NewsBlurAccountDelegate(creds);
            var displayName = string.IsNullOrWhiteSpace(name.Text) ? "NewsBlur" : name.Text.Trim();
            if (await AccountSheetHelpers.CommitAsync(existing, displayName, del, [creds], status) is { } account) return account;
        }
    }
}

public static class AddFeedlyAccountSheet
{
    public static async Task<Account.Account?> ShowAsync(XamlRoot root, Account.Account? existing = null)
    {
        var config = Feedly.FeedlyClientConfig.Load();
        if (config is null)
        {
            var configurationDialog = new ContentDialog
            {
                Title = "Feedly configuration required",
                Content = "Feedly requires an OAuth client ID and secret. Configure FEEDLY_CLIENT_ID and FEEDLY_CLIENT_SECRET, or feedly-client.json in the app data folder, then reopen this dialog. A refresh token alone cannot authorize the app.",
                CloseButtonText = "OK",
                XamlRoot = root,
            };
            WindowThemeHelper.Apply(configurationDialog);
            await configurationDialog.ShowAsync();
            return null;
        }
        var stack = new StackPanel { Spacing = 8 };
        var name = AccountSheetHelpers.LabeledTextBox(stack, "Display Name:", "Feedly");
        var status = AccountSheetHelpers.AddStatus(stack);
        AccountSheetHelpers.Prefill(existing, name);
        stack.Children.Add(new TextBlock
        {
            Text = "Click Sign In to open your browser and authorize WinNewsWire.",
            TextWrapping = TextWrapping.Wrap,
        });

        var dialog = new ContentDialog
        {
            Title = existing is null ? "Feedly" : "Reconnect Feedly",
            Content = stack,
            PrimaryButtonText = "Sign In",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };
        while (true)
        {
            WindowThemeHelper.Apply(dialog);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
            var displayName = string.IsNullOrWhiteSpace(name.Text) ? "Feedly" : name.Text.Trim();

            Feedly.FeedlyOAuthAccessTokenResponse token;
            try { token = await Feedly.FeedlyBrowserAuth.SignInAsync(config, http: null, CancellationToken.None); }
            catch (Exception ex) when (AccountSheetHelpers.IsSetupError(ex)) { status.Text = "Sign-in failed: " + ex.Message; continue; }
            var access = new Credentials(CredentialsType.OAuthAccessToken, token.Id, token.AccessToken);
            var refresh = token.RefreshToken is { Length: > 0 } rt
                ? new Credentials(CredentialsType.OAuthRefreshToken, token.Id, rt) : null;
            var del = new Feedly.FeedlyAccountDelegate(access, refresh, config);
            if (await AccountSheetHelpers.CommitAsync(existing, displayName, del, refresh is null ? [access] : [refresh, access], status) is { } account) return account;
        }
    }
}

public static class AddReaderApiAccountSheet
{
    public static async Task<Account.Account?> ShowAsync(XamlRoot root,
        ReaderAPI.ReaderAPIVariant variant, bool showHost = false, Account.Account? existing = null)
    {
        var stack = new StackPanel { Spacing = 8 };
        var name = AccountSheetHelpers.LabeledTextBox(stack, "Display Name:", variant.ToString());
        var user = AccountSheetHelpers.LabeledTextBox(stack, "Username:", "");
        var pass = AccountSheetHelpers.LabeledPasswordBox(stack, "Password:", "");
        TextBox? host = null;
        if (showHost)
            host = AccountSheetHelpers.LabeledTextBox(stack, "Server URL:", "https://yourfreshrss/api/greader.php");
        var status = AccountSheetHelpers.AddStatus(stack);
        AccountSheetHelpers.Prefill(existing, name, user, host);

        var dialog = new ContentDialog
        {
            Title = existing is null ? variant.ToString() : "Reconnect " + variant,
            Content = stack,
            PrimaryButtonText = existing is null ? "Add" : "Reconnect",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };
        while (true)
        {
            WindowThemeHelper.Apply(dialog);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
            var hostText = host?.Text?.Trim();
            if (showHost && (!Uri.TryCreate(hostText, UriKind.Absolute, out var endpoint) ||
                endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length > 0))
            {
                status.Text = "Enter an HTTP or HTTPS server URL without embedded credentials.";
                continue;
            }
            using var caller = new ReaderAPI.ReaderAPICaller(variant, string.IsNullOrEmpty(hostText) ? null : hostText);
            string? token;
            try { token = await caller.LoginAsync(user.Text.Trim(), pass.Password); }
            catch (Exception ex) when (AccountSheetHelpers.IsSetupError(ex)) { status.Text = ex.Message; continue; }
            if (token is null) { status.Text = "Could not sign in. Check your credentials and try again."; continue; }

            var creds = new Credentials(CredentialsType.ReaderBasic, user.Text.Trim(), pass.Password);
            var del = new ReaderAPI.ReaderAPIAccountDelegate(variant, string.IsNullOrEmpty(hostText) ? null : hostText, creds, token);
            var displayName = string.IsNullOrWhiteSpace(name.Text) ? variant.ToString() : name.Text.Trim();
            if (await AccountSheetHelpers.CommitAsync(existing, displayName, del, [creds], status) is { } account) return account;
        }
    }
}
