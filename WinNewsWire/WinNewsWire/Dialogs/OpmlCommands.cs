using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinNewsWire.Account;
using WinNewsWire.AppRuntime;
using WinNewsWire.AppShared.Exporters;
using WinNewsWire.Parsers;
using WinRT.Interop;

namespace WinNewsWire.Dialogs;

/// <summary>OPML import/export helpers. Ports <c>importOPML</c> / <c>exportOPML</c> menu actions.</summary>
public static class OpmlCommands
{
    public static async Task ImportAsync(IntPtr ownerHwnd)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, ownerHwnd);
        picker.FileTypeFilter.Add(".opml");
        picker.FileTypeFilter.Add(".xml");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        var bytes = await Windows.Storage.FileIO.ReadBufferAsync(file);
        var data = new byte[bytes.Length];
        using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(bytes)) reader.ReadBytes(data);
        var text = System.Text.Encoding.UTF8.GetString(data);
        var doc = OpmlParser.Parse(new ParserData(file.Path, data));
        var account = AppService.Shared.Accounts.DefaultAccount;
        if (account is null) return;
        ImportRecursive(account, doc, folder: null);
    }

    private static void ImportRecursive(Account.Account account, OpmlItem item, Folder? folder)
    {
        foreach (var c in item.Children)
        {
            if (c.FeedSpecifier is { } spec)
                account.AddFeed(spec.FeedUrl, spec.Title, folder);
            else if (c.IsFolder && !string.IsNullOrWhiteSpace(c.Title))
            {
                var sub = folder ?? account.AddFolder(c.Title!);
                ImportRecursive(account, c, sub);
            }
        }
    }

    public static async Task ExportAsync(IntPtr ownerHwnd)
    {
        var account = AppService.Shared.Accounts.DefaultAccount;
        if (account is null) return;
        var picker = new FileSavePicker();
        InitializeWithWindow.Initialize(picker, ownerHwnd);
        picker.SuggestedFileName = "Subscriptions";
        picker.FileTypeChoices.Add("OPML", new List<string> { ".opml" });
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        var text = OpmlExporter.OPMLString(account, account.Name);
        await Windows.Storage.FileIO.WriteTextAsync(file, text);
    }

    /// <summary>Import a NetNewsWire 3 subscriptions.plist by converting it to OPML.</summary>
    public static async Task ImportNnw3Async(IntPtr ownerHwnd)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, ownerHwnd);
        picker.FileTypeFilter.Add(".plist");
        picker.FileTypeFilter.Add("*");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        var opml = await WinNewsWire.AppShared.Importers.Nnw3Importer.ConvertToOpmlAsync(file.Path);
        if (opml is null) return;
        var bytes = System.Text.Encoding.UTF8.GetBytes(opml);
        var doc = OpmlParser.Parse(new ParserData(file.Path, bytes));
        var account = AppService.Shared.Accounts.DefaultAccount;
        if (account is null) return;
        ImportRecursive(account, doc, folder: null);
    }
}
