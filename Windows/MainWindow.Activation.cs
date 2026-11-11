using System;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.UI.Xaml;
using Windows.Storage;
using WinNewsWire.Core;

namespace WinNewsWire;

public sealed partial class MainWindow
{
    internal AppShared.Scripting.ScriptingSelection CaptureScriptingSelection()
    {
        var model = MainContentControl.ViewModel;
        return new(model.SelectedArticle?.SourceArticle,
            model.SelectedArticles.Select(article => article.SourceArticle).OfType<Articles.Article>().ToArray(),
            model.SelectedSidebarItems.Where(item => item.Tag is Account.Feed)
                .Select(item => ((Account.Feed)item.Tag!, item.ParentFolder?.FolderID)).ToArray());
    }

    internal async Task<bool> HandleSharedFeedAsync(string url)
    {
        await MainContentControl.Initialization;
        await Restoration;
        if (_closed || MainContentControl.IsShuttingDown) throw new InvalidOperationException("The subscription window is closing.");
        Activate();
        return await MainContentControl.SubscribeSharedFeedAsync(url);
    }

    internal async Task RevealFeedAsync(string accountID, string feedID)
    {
        await MainContentControl.Initialization;
        await Restoration;
        if (_closed || MainContentControl.IsShuttingDown) throw new InvalidOperationException("The main window is closing.");
        var model = MainContentControl.ViewModel;
        var item = model.FindSidebarItemForFeed(accountID, feedID)
            ?? throw new InvalidOperationException("The feed is not available in an active account.");
        model.IsSidebarVisible = true;
        model.SetSelectedSidebarItems([item]);
        model.SearchQuery = "";
        await model.FilterArticlesAsync();
        Activate();
    }

    internal async Task HandleActivationAsync(AppActivationRequest request, StorageFile? file = null)
    {
        await MainContentControl.Initialization;
        await Restoration;
        if (_closed || MainContentControl.IsShuttingDown) throw new InvalidOperationException("The window is closing.");
        Activate();
        switch (request.Kind)
        {
            case AppActivationKind.ShowMainWindow:
                return;
            case AppActivationKind.AddFeed:
                await MainContentControl.NewFeedAsync(request.Target);
                return;
            case AppActivationKind.ImportOpml:
                file ??= await StorageFile.GetFileFromPathAsync(request.Target ?? throw new ArgumentException("A subscription file is required."));
                await Dialogs.OpmlCommands.ImportFileAsync(file, MainContentControl.XamlRoot);
                return;
            case AppActivationKind.InstallThemeFile:
                var path = file?.Path ?? request.Target;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    throw new IOException("Open a local .nnwtheme or ZIP theme archive.");
                await InstallThemeAsync(path, null);
                return;
            case AppActivationKind.InstallThemeUrl:
                await InstallThemeAsync(null, new Uri(request.Target ?? throw new ArgumentException("A theme URL is required.")));
                return;
            case AppActivationKind.ShowUnread:
            case AppActivationKind.ShowToday:
            case AppActivationKind.ShowStarred:
                await MainContentControl.ViewModel.RevealActivationAsync(request);
                return;
            default:
                throw new ArgumentException("The activation action is not supported.", nameof(request));
        }
    }

    private async Task InstallThemeAsync(string? path, Uri? url)
    {
        var manager = AppShared.ArticleThemes.ArticleThemesManager.Shared;
        var theme = url is not null
            ? await AppShared.ArticleThemes.ArticleThemeDownloader.InstallFromUrlAsync(url, manager)
            : await Task.Run(() => manager.InstallFromZip(path ?? throw new ArgumentException("A theme archive is required.")));
        if (_closed || MainContentControl.IsShuttingDown) return;
        manager.CurrentTheme = theme;
        PopulateThemesMenu();
        MainContentControl.ViewModel.RefreshArticleHtml();
    }
}
