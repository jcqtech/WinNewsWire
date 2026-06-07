using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinNewsWire.Account;

namespace WinNewsWire.Dialogs.AccountSheets;

/// <summary>Per-backend account setup sheets. Ports Mac NNW's <c>AccountsAddLocal.xib</c>,
/// <c>AccountsFeedbin.xib</c>, <c>AccountsReaderAPI.xib</c>, <c>AccountsNewsBlur.xib</c>, plus
/// a dedicated Feedly sheet that triggers the OAuth loopback sign-in.</summary>
public static class AccountTypePickerDialog
{
    public sealed record AccountOption(AccountType Type, string Label, string Description, string Glyph);

    public static readonly IReadOnlyList<AccountOption> AllOptions = new[]
    {
        new AccountOption(AccountType.OnMyMac, "On My PC", "Local account; feeds stored on this computer.", "\uE7C4"),
        new AccountOption(AccountType.Feedbin, "Feedbin", "Sync subscriptions and read state with Feedbin.", "\uE7C3"),
        new AccountOption(AccountType.Feedly, "Feedly", "Sign in to Feedly via your browser (OAuth).", "\uE7C3"),
        new AccountOption(AccountType.NewsBlur, "NewsBlur", "Sync with NewsBlur using your account credentials.", "\uE7C3"),
        new AccountOption(AccountType.FreshRSS, "FreshRSS", "Self-hosted FreshRSS (Google Reader-compatible API).", "\uE7C3"),
        new AccountOption(AccountType.Inoreader, "Inoreader", "Sync with Inoreader (Reader API).", "\uE7C3"),
        new AccountOption(AccountType.BazQux, "BazQux", "Sync with BazQux Reader.", "\uE7C3"),
        new AccountOption(AccountType.TheOldReader, "The Old Reader", "Sync with The Old Reader.", "\uE7C3"),
    };

    public static async Task<Account.Account?> ShowAsync(XamlRoot root)
    {
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            MinWidth = 420,
            Height = 340,
        };
        foreach (var o in AllOptions)
        {
            var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            stack.Children.Add(new FontIcon { Glyph = o.Glyph, FontSize = 24 });
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = o.Label, Style = (Style)App.Current.Resources["BodyStrongTextBlockStyle"] });
            text.Children.Add(new TextBlock
            {
                Text = o.Description,
                Style = (Style)App.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)App.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
            stack.Children.Add(text);
            list.Items.Add(new ListViewItem { Content = stack, Tag = o });
        }
        list.SelectedIndex = 0;

        var dialog = new ContentDialog
        {
            Title = "Add Account",
            Content = list,
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        if (list.SelectedItem is not ListViewItem lvi || lvi.Tag is not AccountOption choice) return null;

        return choice.Type switch
        {
            AccountType.OnMyMac => await AddLocalAccountSheet.ShowAsync(root),
            AccountType.Feedbin => await AddFeedbinAccountSheet.ShowAsync(root),
            AccountType.NewsBlur => await AddNewsBlurAccountSheet.ShowAsync(root),
            AccountType.Feedly => await AddFeedlyAccountSheet.ShowAsync(root),
            AccountType.FreshRSS => await AddReaderApiAccountSheet.ShowAsync(root, ReaderAPI.ReaderAPIVariant.FreshRSS, showHost: true),
            AccountType.Inoreader => await AddReaderApiAccountSheet.ShowAsync(root, ReaderAPI.ReaderAPIVariant.Inoreader),
            AccountType.BazQux => await AddReaderApiAccountSheet.ShowAsync(root, ReaderAPI.ReaderAPIVariant.BazQux),
            AccountType.TheOldReader => await AddReaderApiAccountSheet.ShowAsync(root, ReaderAPI.ReaderAPIVariant.TheOldReader),
            _ => null,
        };
    }
}
