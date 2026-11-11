using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinNewsWire.Account;
using WinNewsWire.AppRuntime;
using WinNewsWire.AppShared.Exporters;
using WinNewsWire.AppWindows;
using WinNewsWire.Parsers;
using WinRT.Interop;

namespace WinNewsWire.Dialogs;

/// <summary>OPML import/export helpers. Ports <c>importOPML</c> / <c>exportOPML</c> menu actions.</summary>
public static class OpmlCommands
{
    public static async Task ImportAsync(IntPtr ownerHwnd, XamlRoot root)
    {
        try
        {
            var account = await ChooseAccountAsync(root, importing: true);
            if (account is null) return;
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, ownerHwnd);
            picker.FileTypeFilter.Add(".opml");
            picker.FileTypeFilter.Add(".xml");
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            await ReadAndImportFileAsync(account, file, root);
        }
        catch (Exception ex) when (IsFileOrAccountError(ex))
        {
            await ShowErrorAsync(root, "Import OPML", ex);
        }
    }

    public static async Task ImportFileAsync(StorageFile file, XamlRoot root)
    {
        try
        {
            var account = await ChooseAccountAsync(root, importing: true);
            if (account is null) return;
            await ReadAndImportFileAsync(account, file, root);
        }
        catch (Exception error) when (IsFileOrAccountError(error))
        {
            await ShowErrorAsync(root, "Import OPML", error);
        }
    }

    private static async Task ReadAndImportFileAsync(Account.Account account, StorageFile file, XamlRoot root)
    {
        var buffer = await FileIO.ReadBufferAsync(file);
        var data = new byte[buffer.Length];
        using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer)) reader.ReadBytes(data);
        await ImportWithProgressAsync(account, new ParserData(string.IsNullOrEmpty(file.Path) ? file.Name : file.Path, data), root);
    }

    public static async Task ExportAsync(IntPtr ownerHwnd, XamlRoot root)
    {
        try
        {
            var account = await ChooseAccountAsync(root, importing: false);
            if (account is null) return;
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(ownerHwnd));
            var invalid = Path.GetInvalidFileNameChars();
            var name = string.Concat(account.NameForDisplay.Where(c => !char.IsWhiteSpace(c) && !invalid.Contains(c)));
            picker.SuggestedFileName = "Subscriptions-" + (name.Length == 0 ? "Account" : name);
            picker.DefaultFileExtension = ".opml";
            picker.FileTypeChoices.Add("OPML", new List<string> { ".opml" });
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            var text = OpmlExporter.OPMLString(account, Path.GetFileName(file.Path));
            await Core.FileUtilities.WriteAllBytesAtomicallyAsync(file.Path, Encoding.UTF8.GetBytes(text));
        }
        catch (Exception ex) when (IsFileOrAccountError(ex))
        {
            await ShowErrorAsync(root, "Export OPML", ex);
        }
    }

    /// <summary>Import a NetNewsWire 3 subscriptions.plist by converting it to OPML.</summary>
    public static async Task ImportNnw3Async(IntPtr ownerHwnd, XamlRoot root)
    {
        try
        {
            var account = await ChooseAccountAsync(root, importing: true);
            if (account is null) return;
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, ownerHwnd);
            picker.FileTypeFilter.Add(".plist");
            picker.FileTypeFilter.Add("*");
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            var opml = await AppShared.Importers.Nnw3Importer.ConvertToOpmlAsync(file.Path)
                ?? throw new InvalidDataException("This is not a supported NetNewsWire 3 XML subscriptions file. Binary property lists must first be converted to XML.");
            await ImportWithProgressAsync(account, new ParserData(file.Path, Encoding.UTF8.GetBytes(opml)), root);
        }
        catch (Exception ex) when (IsFileOrAccountError(ex))
        {
            await ShowErrorAsync(root, "Import NetNewsWire 3 Subscriptions", ex);
        }
    }

    private static async Task<Account.Account?> ChooseAccountAsync(XamlRoot root, bool importing)
    {
        var accounts = AppService.Shared.Accounts.SortedAccounts
            .Where(a => !importing || (a.IsActive && a.SupportsOpmlImport))
            .ToList();
        if (accounts.Count == 0)
            throw new InvalidOperationException(importing
                ? "No active accounts support OPML import. Activate a supported account in Preferences. NewsBlur imports must currently be performed on its website."
                : "There are no accounts to export.");

        var defaults = AppService.Shared.Defaults;
        var savedID = importing ? defaults.ImportOpmlAccountID : defaults.ExportOpmlAccountID;
        var account = accounts.FirstOrDefault(a => a.AccountID == savedID) ?? accounts[0];
        if (accounts.Count > 1)
        {
            var picker = new ComboBox
            {
                Header = "Account",
                ItemsSource = accounts,
                DisplayMemberPath = nameof(Account.Account.NameForDisplay),
                SelectedItem = account,
                MinWidth = 360,
            };
            var dialog = new ContentDialog
            {
                Title = importing ? "Import OPML" : "Export OPML",
                Content = picker,
                PrimaryButtonText = "Continue",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = root,
            };
            WindowThemeHelper.Apply(dialog);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
            account = (Account.Account)picker.SelectedItem;
        }
        if (importing) defaults.ImportOpmlAccountID = account.AccountID;
        else defaults.ExportOpmlAccountID = account.AccountID;
        return account;
    }

    private static async Task ImportWithProgressAsync(Account.Account account, ParserData data, XamlRoot root)
    {
        using var cancellation = new CancellationTokenSource();
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = $"Importing into {account.NameForDisplay} and refreshing articles...", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new ProgressBar { IsIndeterminate = true });
        var dialog = new ContentDialog
        {
            Title = "Importing Subscriptions",
            Content = content,
            CloseButtonText = "Cancel",
            XamlRoot = root,
        };
        dialog.CloseButtonClick += (_, _) => cancellation.Cancel();
        WindowThemeHelper.Apply(dialog);
        var showing = dialog.ShowAsync();
        try { await account.ImportOpmlAsync(data, cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            dialog.Hide();
            await showing;
        }
    }

    private static bool IsFileOrAccountError(Exception error)
        => error is IOException or InvalidDataException or UnauthorizedAccessException or FeedParserException
            or HttpRequestException or JsonException or InvalidOperationException
            or NotSupportedException or COMException or FormatException or TaskCanceledException
            or WinNewsWire.Secrets.CredentialsException;

    private static async Task ShowErrorAsync(XamlRoot root, string operation, Exception error)
    {
        await AppService.Shared.LogAsync(operation, error);
        var dialog = new ContentDialog
        {
            Title = operation + " could not finish",
            Content = string.IsNullOrWhiteSpace(error.Message)
                ? string.Format(System.Globalization.CultureInfo.CurrentCulture, AppShared.AppStrings.Get("OpmlWindowsError"), error.HResult)
                : error.Message,
            CloseButtonText = "OK",
            XamlRoot = root,
        };
        WindowThemeHelper.Apply(dialog);
        await dialog.ShowAsync();
    }
}
