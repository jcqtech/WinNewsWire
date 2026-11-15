using System;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using System.Globalization;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinNewsWire.AppShared;
using Windows.Storage;
using WinNewsWire.Core;

namespace WinNewsWire;

public sealed partial class MainWindow
{
    private readonly CancellationTokenSource _themeImportLifetime = new();
    private Task _themeImportOperation = Task.CompletedTask;
    private ContentDialog? _themeImportDialog;

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

    private Task InstallThemeAsync(string? path, Uri? url)
    {
        if (!_themeImportOperation.IsCompleted) throw new InvalidOperationException("A theme import is already in progress.");
        return _themeImportOperation = InstallThemeCoreAsync(path, url);
    }

    private async Task InstallThemeCoreAsync(string? path, Uri? url)
    {
        _themeImportLifetime.Token.ThrowIfCancellationRequested();
        var manager = AppShared.ArticleThemes.ArticleThemesManager.Shared;
        await using var prepared = url is not null
            ? await AppShared.ArticleThemes.ArticleThemeDownloader.PrepareFromUrlAsync(url, manager, _themeImportLifetime.Token)
            : await Task.Run(() => manager.PrepareImport(path ?? throw new ArgumentException("A theme archive is required.")),
                _themeImportLifetime.Token);
        if (_closed || MainContentControl.IsShuttingDown || _themeImportLifetime.IsCancellationRequested) return;
        var theme = prepared.Theme;
        if (manager.Themes.Any(existing => existing.IsAppTheme
            && string.Equals(existing.Name, theme.Name, StringComparison.OrdinalIgnoreCase)))
        {
            await ShowThemeImportDialogAsync(new ContentDialog
            {
                Title = AppStrings.Get("ThemeAlreadyBundledTitle"),
                Content = string.Format(CultureInfo.CurrentCulture, AppStrings.Get("ThemeAlreadyBundled"), theme.Name),
                CloseButtonText = AppStrings.Get("Cancel")
            });
            return;
        }
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = string.Format(CultureInfo.CurrentCulture, AppStrings.Get("ThemeVersion"), theme.Version) });
        content.Children.Add(new TextBlock
        {
            Text = string.Format(CultureInfo.CurrentCulture, AppStrings.Get("ThemeAuthorWebsite"), theme.CreatorHomePage),
            TextWrapping = TextWrapping.Wrap, MaxWidth = 420
        });
        if (await ShowThemeImportDialogAsync(new ContentDialog
        {
            Title = string.Format(CultureInfo.CurrentCulture, AppStrings.Get("ThemeImportTitle"), theme.Name, theme.CreatorName),
            Content = content,
            PrimaryButtonText = AppStrings.Get("ThemeInstall"),
            CloseButtonText = AppStrings.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close
        }) != ContentDialogResult.Primary) return;
        if (prepared.ReplacesExisting && await ShowThemeImportDialogAsync(new ContentDialog
        {
            Title = AppStrings.Get("ThemeReplaceTitle"),
            Content = string.Format(CultureInfo.CurrentCulture, AppStrings.Get("ThemeReplaceConfirmation"), theme.Name),
            PrimaryButtonText = AppStrings.Get("ThemeOverwrite"),
            CloseButtonText = AppStrings.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close
        }) != ContentDialogResult.Primary) return;
        _themeImportLifetime.Token.ThrowIfCancellationRequested();
        var installed = await Task.Run(() => prepared.Install(overwriteExisting: prepared.ReplacesExisting), _themeImportLifetime.Token);
        if (_closed || MainContentControl.IsShuttingDown || _themeImportLifetime.IsCancellationRequested) return;
        manager.SelectByName(installed.Name);
        MainContentControl.ViewModel.StatusMessage = string.Format(CultureInfo.CurrentCulture,
            AppStrings.Get("ThemeInstalled"), installed.Name);
    }

    private async Task<ContentDialogResult> ShowThemeImportDialogAsync(ContentDialog dialog)
    {
        if (_closed || MainContentControl.IsShuttingDown || _themeImportLifetime.IsCancellationRequested)
            return ContentDialogResult.None;
        dialog.XamlRoot = MainContentControl.XamlRoot;
        _themeImportDialog = dialog;
        try { return await dialog.ShowAsync(); }
        finally { _themeImportDialog = null; }
    }

    private async Task StopThemeImportAsync()
    {
        _themeImportLifetime.Cancel();
        _themeImportDialog?.Hide();
        try { await _themeImportOperation; }
        catch (OperationCanceledException) when (_themeImportLifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            App.LogException("Finish theme import before shutdown", error);
        }
    }
}
