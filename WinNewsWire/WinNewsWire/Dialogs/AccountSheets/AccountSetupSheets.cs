using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinNewsWire.Account;
using WinNewsWire.AppRuntime;
using WinNewsWire.Secrets;

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
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        var displayName = string.IsNullOrWhiteSpace(name.Text) ? "On My PC" : name.Text.Trim();
        return AppService.Shared.Accounts.CreateRemoteAccount(AccountType.OnMyMac, displayName, new LocalAccountDelegate());
    }
}

public static class AddFeedbinAccountSheet
{
    public static async Task<Account.Account?> ShowAsync(XamlRoot root)
    {
        var stack = new StackPanel { Spacing = 8 };
        var name = AccountSheetHelpers.LabeledTextBox(stack, "Display Name:", "Feedbin");
        var user = AccountSheetHelpers.LabeledTextBox(stack, "Email:", "you@example.com");
        var pass = AccountSheetHelpers.LabeledPasswordBox(stack, "Password:", "Feedbin password");
        var status = AccountSheetHelpers.AddStatus(stack);

        var dialog = new ContentDialog
        {
            Title = "Feedbin",
            Content = stack,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };
        while (true)
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
            var creds = new Credentials(CredentialsType.Basic, user.Text.Trim(), pass.Password);
            var del = new Feedbin.FeedbinAccountDelegate(creds);
            bool ok;
            try { ok = await del.ValidateCredentialsAsync(CancellationToken.None); }
            catch (Exception ex) { status.Text = ex.Message; continue; }
            if (!ok) { status.Text = "Could not sign in. Check your credentials and try again."; continue; }
            var displayName = string.IsNullOrWhiteSpace(name.Text) ? "Feedbin" : name.Text.Trim();
            var account = AppService.Shared.Accounts.CreateRemoteAccount(AccountType.Feedbin, displayName, del);
            try { CredentialsManager.Store(creds, "Feedbin"); } catch { }
            return account;
        }
    }
}

public static class AddNewsBlurAccountSheet
{
    public static async Task<Account.Account?> ShowAsync(XamlRoot root)
    {
        var stack = new StackPanel { Spacing = 8 };
        var name = AccountSheetHelpers.LabeledTextBox(stack, "Display Name:", "NewsBlur");
        var user = AccountSheetHelpers.LabeledTextBox(stack, "Username:", "");
        var pass = AccountSheetHelpers.LabeledPasswordBox(stack, "Password:", "");
        var status = AccountSheetHelpers.AddStatus(stack);

        var dialog = new ContentDialog
        {
            Title = "NewsBlur",
            Content = stack,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };
        while (true)
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
            var caller = new NewsBlur.NewsBlurAPICaller();
            string? sessionId;
            try { sessionId = await caller.LoginAsync(user.Text.Trim(), pass.Password); }
            catch (Exception ex) { status.Text = ex.Message; continue; }
            if (sessionId is null) { status.Text = "Could not sign in. Check your credentials and try again."; continue; }
            var creds = new Credentials(CredentialsType.NewsBlurSessionId, user.Text.Trim(), sessionId);
            var del = new NewsBlur.NewsBlurAccountDelegate(creds);
            var displayName = string.IsNullOrWhiteSpace(name.Text) ? "NewsBlur" : name.Text.Trim();
            var account = AppService.Shared.Accounts.CreateRemoteAccount(AccountType.NewsBlur, displayName, del);
            try { CredentialsManager.Store(creds, "NewsBlur"); } catch { }
            return account;
        }
    }
}

public static class AddFeedlyAccountSheet
{
    public static async Task<Account.Account?> ShowAsync(XamlRoot root)
    {
        var config = Feedly.FeedlyClientConfig.Load();
        var stack = new StackPanel { Spacing = 8 };
        var name = AccountSheetHelpers.LabeledTextBox(stack, "Display Name:", "Feedly");
        PasswordBox? refreshBox = null;
        var status = AccountSheetHelpers.AddStatus(stack);

        if (config is null)
        {
            status.Text = "No Feedly client configuration found. Set FEEDLY_CLIENT_ID / FEEDLY_CLIENT_SECRET "
                        + "environment variables or drop feedly-client.json into %LocalAppData%/WinNewsWire. "
                        + "You can paste a pre-existing refresh token below instead.";
            refreshBox = AccountSheetHelpers.LabeledPasswordBox(stack, "Refresh Token (optional):", "feedly refresh token");
        }
        else
        {
            stack.Children.Add(new TextBlock
            {
                Text = "Click Sign In to open your browser and authorize WinNewsWire.",
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var dialog = new ContentDialog
        {
            Title = "Feedly",
            Content = stack,
            PrimaryButtonText = config is null ? "Add" : "Sign In",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };
        while (true)
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
            var displayName = string.IsNullOrWhiteSpace(name.Text) ? "Feedly" : name.Text.Trim();

            if (config is not null && (refreshBox is null || string.IsNullOrEmpty(refreshBox.Password)))
            {
                Feedly.FeedlyOAuthAccessTokenResponse token;
                try { token = await Feedly.FeedlyBrowserAuth.SignInAsync(config, http: null, CancellationToken.None); }
                catch (Exception ex) { status.Text = "Sign-in failed: " + ex.Message; continue; }
                var access = new Credentials(CredentialsType.OAuthAccessToken, token.Id, token.AccessToken);
                var refresh = token.RefreshToken is { Length: > 0 } rt
                    ? new Credentials(CredentialsType.OAuthRefreshToken, token.Id, rt) : null;
                var del = new Feedly.FeedlyAccountDelegate(access, refresh, config);
                if (refresh is not null) { try { CredentialsManager.Store(refresh, "Feedly"); } catch { } }
                try { CredentialsManager.Store(access, "Feedly"); } catch { }
                return AppService.Shared.Accounts.CreateRemoteAccount(AccountType.Feedly, displayName, del);
            }

            var token2 = refreshBox?.Password ?? "";
            if (string.IsNullOrWhiteSpace(token2)) { status.Text = "Provide a refresh token to continue."; continue; }
            var manualRefresh = new Credentials(CredentialsType.OAuthRefreshToken, "feedly", token2);
            try { CredentialsManager.Store(manualRefresh, "Feedly"); } catch { }
            return AppService.Shared.Accounts.CreateRemoteAccount(AccountType.Feedly, displayName,
                new Feedly.FeedlyAccountDelegate(accessToken: null, refreshToken: manualRefresh, config: config));
        }
    }
}

public static class AddReaderApiAccountSheet
{
    public static async Task<Account.Account?> ShowAsync(XamlRoot root,
        ReaderAPI.ReaderAPIVariant variant, bool showHost = false)
    {
        var stack = new StackPanel { Spacing = 8 };
        var name = AccountSheetHelpers.LabeledTextBox(stack, "Display Name:", variant.ToString());
        var user = AccountSheetHelpers.LabeledTextBox(stack, "Username:", "");
        var pass = AccountSheetHelpers.LabeledPasswordBox(stack, "Password:", "");
        TextBox? host = null;
        if (showHost)
            host = AccountSheetHelpers.LabeledTextBox(stack, "Server URL:", "https://yourfreshrss/api/greader.php");
        var status = AccountSheetHelpers.AddStatus(stack);

        var dialog = new ContentDialog
        {
            Title = variant.ToString(),
            Content = stack,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };
        while (true)
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
            var hostText = host?.Text?.Trim();
            var caller = new ReaderAPI.ReaderAPICaller(variant, string.IsNullOrEmpty(hostText) ? null : hostText);
            string? token;
            try { token = await caller.LoginAsync(user.Text.Trim(), pass.Password); }
            catch (Exception ex) { status.Text = ex.Message; continue; }
            if (token is null) { status.Text = "Could not sign in. Check your credentials and try again."; continue; }

            var creds = new Credentials(CredentialsType.ReaderBasic, user.Text.Trim(), pass.Password);
            var del = new ReaderAPI.ReaderAPIAccountDelegate(variant, string.IsNullOrEmpty(hostText) ? null : hostText, creds, token);
            var accountType = variant switch
            {
                ReaderAPI.ReaderAPIVariant.Inoreader => AccountType.Inoreader,
                ReaderAPI.ReaderAPIVariant.BazQux => AccountType.BazQux,
                ReaderAPI.ReaderAPIVariant.TheOldReader => AccountType.TheOldReader,
                _ => AccountType.FreshRSS,
            };
            var displayName = string.IsNullOrWhiteSpace(name.Text) ? variant.ToString() : name.Text.Trim();
            var account = AppService.Shared.Accounts.CreateRemoteAccount(accountType, displayName, del);
            try { CredentialsManager.Store(creds, accountType.ToString()); } catch { }
            return account;
        }
    }
}
