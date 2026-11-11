using System;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinNewsWire.Account;
using WinNewsWire.AppRuntime;
using WinNewsWire.ViewModels;
using WinNewsWire.AppWindows;
using WinNewsWire.Core;
using WinNewsWire.AppShared.Extensions;
using Microsoft.UI.Xaml.Automation;

namespace WinNewsWire.Dialogs;

/// <summary>
/// Port of <c>Mac/MainWindow/AddFeed/</c>. A <see cref="ContentDialog"/> that collects a feed URL,
/// optional custom name, target folder, and target account. Returns an <see cref="AddFeedRequest"/>
/// that the view-model can hand to <see cref="Account.CreateFeedAsync"/>.
/// </summary>
public static class AddFeedDialog
{
    public static async Task<AddFeedRequest?> ShowAsync(XamlRoot root, string? prefilledUrl = null,
        Account.Account? initialAccount = null, Folder? initialFolder = null, string? prefilledName = null)
    {
        var accounts = AppService.Shared.Accounts.SortedActiveAccounts.ToList();
        if (accounts.Count == 0) throw new InvalidOperationException("Add or activate an account before subscribing to a feed.");
        var defaults = AppDefaults.Shared;
        var preferred = AddFeedDefaultContainer.Resolve(accounts, defaults);
        if (initialAccount is not null && accounts.Contains(initialAccount))
            preferred = initialFolder is not null && initialAccount.Folders.Contains(initialFolder)
                ? (initialAccount, initialFolder) : AddFeedDefaultContainer.SubstituteContainerIfNeeded(initialAccount);

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
        var nameBox = new TextBox { PlaceholderText = "(optional custom name)", Text = prefilledName ?? "", MinWidth = 360 };
        var accountCombo = new ComboBox { MinWidth = 360 };
        AutomationProperties.SetName(urlBox, "Feed or website URL");
        AutomationProperties.SetName(nameBox, "Custom feed name");
        AutomationProperties.SetName(accountCombo, "Account");
        foreach (var a in accounts)
            accountCombo.Items.Add(new ComboBoxItem { Content = a.NameForDisplay, Tag = a });
        var selectedId = initialAccount is not null && accounts.Contains(initialAccount)
            ? initialAccount.AccountID : preferred?.Account.AccountID;
        accountCombo.SelectedIndex = Math.Max(0, accounts.FindIndex(account => account.AccountID == selectedId));

        var folderCombo = new ComboBox { MinWidth = 360 };
        AutomationProperties.SetName(folderCombo, "Folder");
        var folderHint = new TextBlock { Text = "Create a folder in this account before adding a feed.", Visibility = Visibility.Collapsed };
        void UpdatePrimaryButton()
        {
            dialog.IsPrimaryButtonEnabled = folderCombo.SelectedIndex >= 0
                && SharedFeedUrl.TryNormalizeUserInput(urlBox.Text, out _);
        }
        void RefillFolders(Folder? selection = null)
        {
            folderCombo.Items.Clear();
            if (((ComboBoxItem)accountCombo.SelectedItem!).Tag is Account.Account acct)
            {
                if (!acct.Type.Has(AccountBehavior.DisallowFeedInRootFolder))
                    folderCombo.Items.Add(new ComboBoxItem { Content = "(top level)" });
                foreach (var f in acct.Folders.OrderBy(folder => folder.NameForDisplay, LocalizedStandardComparer.Shared))
                    folderCombo.Items.Add(new ComboBoxItem { Content = f.NameForDisplay, Tag = f });
            }
            var index = folderCombo.Items.Cast<ComboBoxItem>().ToList().FindIndex(item => ReferenceEquals(item.Tag, selection));
            folderCombo.SelectedIndex = folderCombo.Items.Count > 0 ? Math.Max(0, index) : -1;
            UpdatePrimaryButton();
            folderHint.Visibility = folderCombo.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        RefillFolders(preferred?.Folder);
        accountCombo.SelectionChanged += (_, _) => RefillFolders();
        folderCombo.SelectionChanged += (_, _) => UpdatePrimaryButton();
        urlBox.TextChanged += (_, _) => UpdatePrimaryButton();
        dialog.Opened += (_, _) => urlBox.Focus(FocusState.Programmatic);

        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = "Feed URL:" });
        stack.Children.Add(urlBox);
        stack.Children.Add(new TextBlock { Text = "Name:" });
        stack.Children.Add(nameBox);
        stack.Children.Add(new TextBlock { Text = "Account:" });
        stack.Children.Add(accountCombo);
        stack.Children.Add(new TextBlock { Text = "Folder:" });
        stack.Children.Add(folderCombo);
        stack.Children.Add(folderHint);
        dialog.Content = stack;

        WindowThemeHelper.Apply(dialog);

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return null;
        var url = SharedFeedUrl.NormalizeUserInput(urlBox.Text);

        var selectedAccount = ((ComboBoxItem)accountCombo.SelectedItem!).Tag as Account.Account
            ?? throw new InvalidOperationException("Choose an account for this feed.");
        var selectedFolder = (folderCombo.SelectedItem as ComboBoxItem)?.Tag as Folder;
        AddFeedDefaultContainer.SaveDefaultContainer(selectedAccount, selectedFolder, defaults);

        return new AddFeedRequest
        {
            Url = url,
            Name = string.IsNullOrWhiteSpace(nameBox.Text) ? null : nameBox.Text.Trim(),
            FolderName = selectedFolder?.Name,
            Account = selectedAccount,
        };
    }
}
