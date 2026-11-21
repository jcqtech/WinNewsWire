using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinNewsWire.Account;
using WinNewsWire.Account.OneDrive;
using WinNewsWire.AppRuntime;
using WinNewsWire.AppWindows;

namespace WinNewsWire.Dialogs.AccountSheets;

public static class OneDriveAccountSheet
{
    public static async Task<Account.Account?> ShowAsync(XamlRoot root, Account.Account? existing = null)
    {
        var stack = new StackPanel { Spacing = 10, MaxWidth = 540 };
        stack.Children.Add(new TextBlock
        {
            Text = "Sync subscriptions, folders, read/unread state, starred articles and article content between Windows PCs using OneDrive. Feeds download directly from their publishers.",
            TextWrapping = TextWrapping.Wrap
        });
        var name = AccountSheetHelpers.LabeledTextBox(stack, "Display Name:", "OneDrive");
        AccountSheetHelpers.Prefill(existing, name);
        var roots = OneDriveLocations.FindRoots();
        var folder = AccountSheetHelpers.LabeledTextBox(stack, "Shared OneDrive folder:", @"C:\Users\you\OneDrive\Apps\WinNewsWire");
        folder.Text = existing?.Server is { } server ? new Uri(server).LocalPath
            : roots.Count == 0 ? "" : Path.Combine(roots[0], "Apps", "WinNewsWire");
        var browse = new Button { Content = "Choose Folder..." };
        browse.Click += async (_, _) =>
        {
            var picker = new global::Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker,
                Microsoft.UI.Win32Interop.GetWindowFromWindowId(root.ContentIslandEnvironment.AppWindowId));
            if (await picker.PickSingleFolderAsync() is { } selected) folder.Text = selected.Path;
        };
        stack.Children.Add(browse);
        bool initialUnreadContent = existing?.Settings.OneDriveSyncUnreadContent ?? false;
        var unread = new CheckBox
        {
            Content = "Sync article content for unread articles",
            IsChecked = initialUnreadContent
        };
        stack.Children.Add(unread);
        stack.Children.Add(new TextBlock
        {
            Text = "Starred article content always syncs. Use the same dedicated folder on every PC. In File Explorer, choose \"Always keep on this device\" for this folder. OneDrive must be signed in and syncing; its tray icon shows cloud upload progress. Removing this account only removes this PC's copy. Save changed settings before scanning or cleaning up.",
            TextWrapping = TextWrapping.Wrap
        });
        if (existing?.Delegate is OneDriveAccountDelegate current)
        {
            stack.Children.Add(new TextBlock
            {
                Text = current.LastSyncError is { } error ? "Last sync error: " + error
                    : current.LastExchangeUtc is { } date ? "Last local OneDrive exchange: " + date.ToLocalTime().ToString("g")
                    : "No OneDrive exchange in this session yet.",
                TextWrapping = TextWrapping.Wrap
            });
            stack.Children.Add(new OneDriveSyncPanel(existing));
        }
        var status = AccountSheetHelpers.AddStatus(stack);
        if (roots.Count == 0) status.Text = "Set up OneDrive on this PC first, then reopen this dialog.";
        var dialog = new ContentDialog
        {
            Title = existing is null ? "OneDrive" : "OneDrive Settings",
            Content = new ScrollViewer { Content = stack, MaxHeight = 620 },
            PrimaryButtonText = existing is null ? "Add" : "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
            IsPrimaryButtonEnabled = roots.Count > 0
        };
        while (true)
        {
            WindowThemeHelper.Apply(dialog);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
            try
            {
                var path = OneDriveLocations.ValidateSyncDirectory(folder.Text.Trim(), roots);
                if (AppService.Shared.Accounts.Accounts.Any(account => !ReferenceEquals(account, existing)
                    && account.Type == AccountType.OneDrive && account.Server is { } endpoint
                    && string.Equals(Path.TrimEndingDirectorySeparator(new Uri(endpoint).LocalPath), path, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("This OneDrive folder is already connected to an account on this PC.");
                Directory.CreateDirectory(path);
                var probe = Path.Combine(path, Guid.NewGuid().ToString("N") + ".tmp");
                try { await File.WriteAllTextAsync(probe, "WinNewsWire access check"); }
                finally { File.Delete(probe); }
                var account = existing ?? AppService.Shared.Accounts.CreateRemoteAccount(AccountType.OneDrive,
                    string.IsNullOrWhiteSpace(name.Text) ? "OneDrive" : name.Text.Trim(), new OneDriveAccountDelegate(path));
                bool reconnect = existing is null || existing.Server is null || !string.Equals(
                    Path.TrimEndingDirectorySeparator(new Uri(existing.Server).LocalPath), path, StringComparison.OrdinalIgnoreCase);
                bool? preference = existing is not null
                    ? unread.IsChecked != initialUnreadContent ? unread.IsChecked == true : null
                    : unread.IsChecked == true ? true : null;
                await account.ConfigureOneDriveAsync(path, preference);
                AppService.Shared.Accounts.SaveAccountMeta(account);
                if (reconnect) await InitialSyncAsync(account, root);
                return account;
            }
            catch (Exception error) when (AccountSheetHelpers.IsSetupError(error))
            {
                status.Text = "Could not connect OneDrive: " + error.Message;
            }
        }
    }

    private static async Task InitialSyncAsync(Account.Account account, XamlRoot root)
    {
        using var cancellation = new System.Threading.CancellationTokenSource();
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new ProgressRing { IsActive = true, Width = 32, Height = 32 });
        panel.Children.Add(new TextBlock
        {
            Text = "Synchronizing OneDrive and refreshing feeds. You can cancel; the account and pending changes will be kept.",
            TextWrapping = TextWrapping.Wrap
        });
        var progress = new ContentDialog
        {
            Title = "Connecting OneDrive",
            Content = panel,
            CloseButtonText = "Cancel",
            XamlRoot = root
        };
        progress.CloseButtonClick += (_, _) => cancellation.Cancel();
        WindowThemeHelper.Apply(progress);
        var showing = progress.ShowAsync();
        Exception? failure = null;
        try { await account.RefreshAllAsync(ct: cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (AccountSheetHelpers.IsSetupError(error) || error is InvalidDataException or FormatException)
        {
            failure = error;
            await AppService.Shared.LogAsync("Initial OneDrive synchronization", error);
        }
        finally
        {
            progress.Hide();
            await showing;
        }
        if (failure is null) return;
        var errorDialog = new ContentDialog
        {
            Title = "OneDrive account saved; sync needs attention",
            Content = failure.Message + "\n\nYour local account and changes are kept. Check OneDrive, then use Refresh to retry.",
            CloseButtonText = "OK",
            XamlRoot = root
        };
        WindowThemeHelper.Apply(errorDialog);
        await errorDialog.ShowAsync();
    }
}
