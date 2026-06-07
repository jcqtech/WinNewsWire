using System;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinNewsWire.Account;
using WinNewsWire.AppRuntime;
using WinNewsWire.AppShared.SmartFeeds;
using WinNewsWire.Models;

namespace WinNewsWire.Dialogs;

/// <summary>
/// Port of <c>Mac/Inspector/</c>. NetNewsWire uses a floating HUD-style inspector window;
/// on Windows we surface the same information through a <see cref="ContentDialog"/> that
/// adapts its fields to the selected sidebar item kind (Feed / Folder / SmartFeed / Nothing).
/// </summary>
public static class InspectorDialog
{
    public static Task ShowAsync(XamlRoot root, SidebarItem? selected) => selected?.Tag switch
    {
        Account.Feed feed => ShowFeedAsync(root, feed),
        Folder folder => ShowFolderAsync(root, folder),
        IPseudoFeed smart => ShowSmartFeedAsync(root, smart),
        Account.Account account => ShowAccountAsync(root, account),
        _ => ShowNothingAsync(root),
    };

    private static async Task ShowFeedAsync(XamlRoot root, Account.Feed feed)
    {
        var nameBox = new TextBox { Text = feed.EditedName ?? feed.Name ?? "", MinWidth = 360 };
        var homePageLink = new HyperlinkButton
        {
            Content = feed.HomePageUrl ?? "(none)",
            NavigateUri = !string.IsNullOrEmpty(feed.HomePageUrl) ? new System.Uri(feed.HomePageUrl) : null,
        };
        var urlText = new TextBlock { Text = feed.Url, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var unread = new TextBlock { Text = feed.UnreadCount.ToString() };
        var externalId = new TextBlock
        {
            Text = string.IsNullOrEmpty(feed.ExternalID) ? "(local)" : feed.ExternalID,
            TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
        };

        var stack = new StackPanel { Spacing = 8 };
        AddRow(stack, "Name:", nameBox);
        AddRow(stack, "Home Page:", homePageLink);
        AddRow(stack, "Feed URL:", urlText);
        AddRow(stack, "Unread:", unread);
        AddRow(stack, "External ID:", externalId);

        var dialog = new ContentDialog
        {
            Title = "Feed Info",
            Content = stack,
            PrimaryButtonText = "Save",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var edited = nameBox.Text?.Trim();
            feed.EditedName = string.IsNullOrWhiteSpace(edited) ? null : edited;
            feed.OnDisplayNameChanged();
            var account = AppService.Shared.Accounts.Accounts
                .FirstOrDefault(a => a.AccountID == feed.AccountID);
            try { account?.SaveChanges(); } catch { }
        }
    }

    private static async Task ShowFolderAsync(XamlRoot root, Folder folder)
    {
        var nameBox = new TextBox { Text = folder.Name ?? "", MinWidth = 360 };
        var countText = new TextBlock { Text = folder.Feeds.Count.ToString() };
        var unreadText = new TextBlock { Text = folder.Feeds.Sum(f => f.UnreadCount).ToString() };

        var stack = new StackPanel { Spacing = 8 };
        AddRow(stack, "Name:", nameBox);
        AddRow(stack, "Feeds:", countText);
        AddRow(stack, "Unread:", unreadText);

        var dialog = new ContentDialog
        {
            Title = "Folder Info",
            Content = stack,
            PrimaryButtonText = "Save",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            folder.Name = nameBox.Text?.Trim();
            var account = AppService.Shared.Accounts.Accounts
                .FirstOrDefault(a => a.AccountID == folder.AccountID);
            try { account?.SaveChanges(); } catch { }
        }
    }

    private static async Task ShowSmartFeedAsync(XamlRoot root, IPseudoFeed smart)
    {
        var stack = new StackPanel { Spacing = 8 };
        AddRow(stack, "Name:", new TextBlock { Text = smart.NameForDisplay });
        AddRow(stack, "Unread:", new TextBlock { Text = smart.UnreadCount.ToString() });
        AddRow(stack, "Kind:", new TextBlock { Text = "Smart feed (built-in)" });

        var dialog = new ContentDialog
        {
            Title = "Smart Feed Info",
            Content = stack,
            CloseButtonText = "Close",
            XamlRoot = root,
        };
        await dialog.ShowAsync();
    }

    private static async Task ShowAccountAsync(XamlRoot root, Account.Account account)
    {
        var nameBox = new TextBox { Text = account.Name, MinWidth = 360 };
        var typeText = new TextBlock { Text = account.Type.ToString() };
        var feedCount = new TextBlock { Text = account.FlattenedFeeds().Count().ToString() };
        var folderCount = new TextBlock { Text = account.Folders.Count.ToString() };

        var stack = new StackPanel { Spacing = 8 };
        AddRow(stack, "Name:", nameBox);
        AddRow(stack, "Type:", typeText);
        AddRow(stack, "Feeds:", feedCount);
        AddRow(stack, "Folders:", folderCount);

        var dialog = new ContentDialog
        {
            Title = "Account Info",
            Content = stack,
            PrimaryButtonText = "Save",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(nameBox.Text))
        {
            account.Name = nameBox.Text.Trim();
            AppService.Shared.Accounts.SaveAccountMeta(account);
        }
    }

    private static Task ShowNothingAsync(XamlRoot root)
    {
        var dialog = new ContentDialog
        {
            Title = "Inspector",
            Content = new TextBlock
            {
                Text = "Select a feed, folder, or smart feed in the sidebar to see its details.",
                TextWrapping = TextWrapping.Wrap,
                MinWidth = 320,
            },
            CloseButtonText = "Close",
            XamlRoot = root,
        };
        return dialog.ShowAsync().AsTask();
    }

    private static void AddRow(StackPanel stack, string label, FrameworkElement control)
    {
        stack.Children.Add(new TextBlock { Text = label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        stack.Children.Add(control);
    }
}
