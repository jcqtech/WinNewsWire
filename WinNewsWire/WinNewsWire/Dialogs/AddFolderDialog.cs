using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using WinNewsWire.Account;
using WinNewsWire.AppRuntime;
using WinNewsWire.AppWindows;

namespace WinNewsWire.Dialogs;

/// <summary>Port of <c>AddFolderWindowController</c>.</summary>
public static class AddFolderDialog
{
    public static async Task<Folder?> ShowAsync(Microsoft.UI.Xaml.XamlRoot root)
    {
        var nameBox = new TextBox { PlaceholderText = "Folder Name", MinWidth = 320 };
        var accountCombo = new ComboBox { PlaceholderText = "Account", MinWidth = 320 };
        foreach (var a in AppService.Shared.Accounts.Accounts)
            accountCombo.Items.Add(new ComboBoxItem { Content = a.NameForDisplay, Tag = a });
        if (accountCombo.Items.Count > 0) accountCombo.SelectedIndex = 0;

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
        };

        WindowThemeHelper.Apply(dialog);

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(nameBox.Text)) return null;
        if (accountCombo.SelectedItem is not ComboBoxItem item || item.Tag is not Account.Account acct) return null;
        return acct.AddFolder(nameBox.Text.Trim());
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
