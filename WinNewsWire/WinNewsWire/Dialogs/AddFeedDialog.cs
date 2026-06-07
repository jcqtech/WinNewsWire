using System;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinNewsWire.Account;
using WinNewsWire.AppRuntime;
using WinNewsWire.ViewModels;

namespace WinNewsWire.Dialogs;

/// <summary>
/// Port of <c>Mac/MainWindow/AddFeed/</c>. A <see cref="ContentDialog"/> that collects a feed URL,
/// optional custom name, target folder, and target account. Returns an <see cref="AddFeedRequest"/>
/// that the view-model can hand to <see cref="Account.CreateFeedAsync"/>.
/// </summary>
public static class AddFeedDialog
{
    public static async Task<AddFeedRequest?> ShowAsync(XamlRoot root, string? prefilledUrl = null)
    {
        var accounts = AppService.Shared.Accounts.ActiveAccounts.ToList();
        if (accounts.Count == 0) return null;

        var dialog = new ContentDialog
        {
            Title = "New Feed",
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };

        var urlBox = new TextBox
        {
            PlaceholderText = "https://example.com/feed.xml",
            Text = prefilledUrl ?? "",
            MinWidth = 360,
        };
        var nameBox = new TextBox { PlaceholderText = "(optional custom name)", MinWidth = 360 };
        var accountCombo = new ComboBox { MinWidth = 360 };
        foreach (var a in accounts)
            accountCombo.Items.Add(new ComboBoxItem { Content = a.NameForDisplay, Tag = a });
        accountCombo.SelectedIndex = 0;

        var folderCombo = new ComboBox { MinWidth = 360 };
        void RefillFolders()
        {
            folderCombo.Items.Clear();
            folderCombo.Items.Add(new ComboBoxItem { Content = "(top level)", Tag = null! });
            if (((ComboBoxItem)accountCombo.SelectedItem!).Tag is Account.Account acct)
            {
                foreach (var f in acct.Folders)
                    folderCombo.Items.Add(new ComboBoxItem { Content = f.NameForDisplay, Tag = f });
            }
            folderCombo.SelectedIndex = 0;
        }
        RefillFolders();
        accountCombo.SelectionChanged += (_, _) => RefillFolders();

        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = "Feed URL:" });
        stack.Children.Add(urlBox);
        stack.Children.Add(new TextBlock { Text = "Name:" });
        stack.Children.Add(nameBox);
        stack.Children.Add(new TextBlock { Text = "Account:" });
        stack.Children.Add(accountCombo);
        stack.Children.Add(new TextBlock { Text = "Folder:" });
        stack.Children.Add(folderCombo);
        dialog.Content = stack;

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return null;
        var url = urlBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(url)) return null;

        var selectedAccount = ((ComboBoxItem)accountCombo.SelectedItem!).Tag as Account.Account;
        var selectedFolder = ((ComboBoxItem)folderCombo.SelectedItem!).Tag as Folder;

        return new AddFeedRequest
        {
            Url = url,
            Name = string.IsNullOrWhiteSpace(nameBox.Text) ? null : nameBox.Text.Trim(),
            FolderName = selectedFolder?.Name,
            Account = selectedAccount,
        };
    }
}
