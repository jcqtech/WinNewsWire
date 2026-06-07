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
        OpmlDocument doc;
        try { doc = OpmlParser.Parse(new ParserData(file.Path, data)); }
        catch (FeedParserException) { return; }
        var account = AppService.Shared.Accounts.DefaultAccount;
        if (account is null) return;
        // Batch the import so the sidebar/timeline see a single structural-
        // change event instead of one per feed, matching the Mac app's
        // BatchUpdate.shared.perform { account.loadOPMLItems(...) } path.
        // LoadOpmlItems internally uses PerformBatchUpdate and re-uses
        // existing feeds/folders rather than duplicating them.
        account.LoadOpmlItems(doc.Children);
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
        OpmlDocument doc;
        try { doc = OpmlParser.Parse(new ParserData(file.Path, bytes)); }
        catch (FeedParserException) { return; }
        var account = AppService.Shared.Accounts.DefaultAccount;
        if (account is null) return;
        account.LoadOpmlItems(doc.Children);
    }
}
