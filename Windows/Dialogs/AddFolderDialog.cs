using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using WinNewsWire.Account;
using WinNewsWire.AppRuntime;
using WinNewsWire.AppWindows;
using WinNewsWire.AppShared.Extensions;
using WinNewsWire.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;

namespace WinNewsWire.Dialogs;

/// <summary>Port of <c>AddFolderWindowController</c>.</summary>
public static class AddFolderDialog
{
    public static async Task<Folder?> ShowAsync(Microsoft.UI.Xaml.XamlRoot root)
    {
        var nameBox = new TextBox { PlaceholderText = "Folder Name", MinWidth = 320 };
        var accountCombo = new ComboBox { PlaceholderText = "Account", MinWidth = 320 };
        var accounts = AppService.Shared.Accounts.SortedActiveAccounts
            .Where(a => !a.Type.Has(AccountBehavior.DisallowFolderManagement)).ToList();
        var preferred = AddFolderDefaultAccount.Resolve(accounts, AppDefaults.Shared)
            ?? throw new InvalidOperationException("Add or activate an account that supports folders first.");
        AutomationProperties.SetName(nameBox, "Folder name");
        AutomationProperties.SetName(accountCombo, "Account");
        foreach (var a in accounts)
            accountCombo.Items.Add(new ComboBoxItem { Content = a.NameForDisplay, Tag = a });
        accountCombo.SelectedIndex = accounts.IndexOf(preferred);

        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(new TextBlock { Text = "Folder Name:" });
        stack.Children.Add(nameBox);
        stack.Children.Add(new TextBlock { Text = "Account:" });
        stack.Children.Add(accountCombo);

        var dialog = new ContentDialog
        {
            Title = "New Folder",
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
            Content = stack,
            IsPrimaryButtonEnabled = false,
        };
        nameBox.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(nameBox.Text)
            && accountCombo.SelectedItem is ComboBoxItem { Tag: Account.Account };
        dialog.Opened += (_, _) => nameBox.Focus(FocusState.Programmatic);

        WindowThemeHelper.Apply(dialog);

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return null;
        if (string.IsNullOrWhiteSpace(nameBox.Text)) throw new InvalidOperationException("Enter a folder name.");
        if (accountCombo.SelectedItem is not ComboBoxItem { Tag: Account.Account acct }
            || !AppService.Shared.Accounts.ActiveAccounts.Any(account => ReferenceEquals(account, acct)))
            throw new InvalidOperationException("The selected account is no longer available.");
        AddFolderDefaultAccount.Save(acct, AppDefaults.Shared);
        return await acct.CreateFolderAsync(nameBox.Text.Trim());
    }
}

/// <summary>Port of <c>RenameWindowController</c>.</summary>
public static class RenameDialog
{
    public static async Task<string?> ShowAsync(Microsoft.UI.Xaml.XamlRoot root, string title, string currentName)
    {
        var nameBox = new TextBox { Text = currentName, MinWidth = 320 };
        var dialog = new ContentDialog
        {
            Title = title,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
            Content = nameBox,
        };
        WindowThemeHelper.Apply(dialog);
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return null;
        return string.IsNullOrWhiteSpace(nameBox.Text) ? null : nameBox.Text.Trim();
    }
}
