using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using WinNewsWire.AppShared;

namespace WinNewsWire;

public sealed partial class MainContent
{
    private enum ArticleFindOperation { Start, Next, Previous }
    private CoreWebView2Find? _articleFind;
    private Task _findTask = Task.CompletedTask;
    private CancellationTokenSource? _findCancellation;
    private int _findGeneration;
    private int _activeFindGeneration = -1;
    private bool _findDocumentReady;
    private bool _findSessionActive;

#if PARITY_PROBE
    internal CoreWebView2Find? ProbeArticleFind => _articleFind;
    internal Task ProbeFindCompletion => _findTask;
    internal Func<CoreWebView2FindOptions, CancellationToken, Task>? ProbeFindStart { get; set; }
#endif

    private bool IsArticleFindVisible => ArticleFindBar.Visibility == Visibility.Visible;

    private void InitializeArticleFindControls()
    {
        ArticleFindText.PlaceholderText = AppStrings.Get("FindInArticle");
        AutomationProperties.SetName(ArticleFindText, ArticleFindText.PlaceholderText);
        AutomationProperties.SetName(ArticleFindPrevious, AppStrings.Get("FindPrevious"));
        AutomationProperties.SetName(ArticleFindNext, AppStrings.Get("FindNext"));
        AutomationProperties.SetName(ArticleFindOptionsButton, AppStrings.Get("FindOptions"));
        AutomationProperties.SetName(ArticleFindClose, AppStrings.Get("CloseFind"));
        ArticleFindMatchCase.Text = AppStrings.Get("MatchCase");
        ArticleFindWholeWord.Text = AppStrings.Get("WholeWords");
        ToolTipService.SetToolTip(ArticleFindWholeWord, AppStrings.Get("FindWholeWordsHelp"));
    }

    public async Task ShowArticleFindAsync()
    {
        if (_shuttingDown) return;
        if (!HasDisplayedDocument)
        {
            ViewModel.StatusMessage = AppStrings.Get("SelectArticleForFind");
            return;
        }
        bool opening = !IsArticleFindVisible;
        ArticleFindBar.Visibility = Visibility.Visible;
        ArticleFindText.Focus(FocusState.Programmatic);
        ArticleFindText.SelectAll();
        if (opening) await QueueArticleFindAsync(ArticleFindOperation.Start, reset: true);
    }

    public async Task FindNextInArticleAsync(bool backwards = false)
    {
        if (_shuttingDown || !HasDisplayedDocument) return;
        ArticleFindBar.Visibility = Visibility.Visible;
        if (ArticleFindText.Text.Length == 0)
        {
            ArticleFindText.Focus(FocusState.Programmatic);
            return;
        }
        await QueueArticleFindAsync(backwards ? ArticleFindOperation.Previous : ArticleFindOperation.Next);
    }

    public async Task UseArticleSelectionForFindAsync()
    {
        if (!_webViewReady || _shuttingDown || !HasDisplayedDocument) return;
        ulong navigation = ActiveNavigationID;
        var browser = ActiveWebView;
        try
        {
            string? selected = JsonSerializer.Deserialize<string>(await browser.ExecuteScriptAsync("window.getSelection()?.toString() ?? ''"));
            if (_shuttingDown || navigation != ActiveNavigationID || !ReferenceEquals(browser, ActiveWebView)) return;
            if (string.IsNullOrEmpty(selected))
            {
                ViewModel.StatusMessage = AppStrings.Get("SelectTextForFind");
                return;
            }
            ArticleFindBar.Visibility = Visibility.Visible;
            if (ArticleFindText.Text == selected) await QueueArticleFindAsync(ArticleFindOperation.Start, reset: true);
            else ArticleFindText.Text = selected;
            ArticleFindText.Focus(FocusState.Programmatic);
            ArticleFindText.SelectAll();
        }
        catch (Exception error) when (IsArticleFindError(error)) { ReportArticleFindError(error); }
    }

    public async Task JumpToArticleSelectionAsync()
    {
        if (!_webViewReady || _shuttingDown || !HasDisplayedDocument) return;
        ulong navigation = ActiveNavigationID;
        var browser = ActiveWebView;
        try
        {
            bool selected = JsonSerializer.Deserialize<bool>(await browser.ExecuteScriptAsync("""
                (() => {
                    const selection = window.getSelection();
                    if (!selection || selection.rangeCount === 0 || selection.isCollapsed) return false;
                    const rect = selection.getRangeAt(0).getBoundingClientRect();
                    window.scrollBy(0, rect.top - (window.innerHeight - rect.height) / 2);
                    return true;
                })()
                """));
            if (!_shuttingDown && navigation == ActiveNavigationID && ReferenceEquals(browser, ActiveWebView))
            {
                if (selected) browser.Focus(FocusState.Programmatic);
                else ViewModel.StatusMessage = AppStrings.Get("SelectTextForFind");
            }
        }
        catch (Exception error) when (IsArticleFindError(error)) { ReportArticleFindError(error); }
    }

    public void HideArticleFind()
    {
        ArticleFindBar.Visibility = Visibility.Collapsed;
        StopArticleFind();
        if (!_shuttingDown && HasDisplayedDocument) ActiveWebView.Focus(FocusState.Programmatic);
    }

    private void StopArticleFind(bool detach = false)
    {
        _findGeneration++;
        CancelPendingArticleFind();
        _findSessionActive = false;
        ArticleFindPrevious.IsEnabled = ArticleFindNext.IsEnabled = false;
        ArticleFindStatus.Text = "";
        if (_articleFind is null) return;
        try { _articleFind.Stop(); }
        catch (Exception error) when (IsArticleFindError(error)) { ReportArticleFindError(error); }
        if (detach)
        {
            _articleFind.MatchCountChanged -= ArticleFindResultChanged;
            _articleFind.ActiveMatchIndexChanged -= ArticleFindResultChanged;
            _articleFind = null;
        }
    }

    private void CancelPendingArticleFind()
    {
        _findCancellation?.Cancel();
        _findCancellation?.Dispose();
        _findCancellation = null;
    }

    private CoreWebView2Find GetArticleFind()
    {
        if (_articleFind is not null) return _articleFind;
        var core = ActiveWebView.CoreWebView2;
        try { _articleFind = core.Find; }
        catch (Exception error) when (error is NotImplementedException or InvalidCastException
            || error is COMException { HResult: unchecked((int)0x80004002) })
        {
            throw new NotSupportedException(AppStrings.Get("FindRuntimeRequired"), error);
        }
        _articleFind.MatchCountChanged += ArticleFindResultChanged;
        _articleFind.ActiveMatchIndexChanged += ArticleFindResultChanged;
        return _articleFind;
    }

    private Task QueueArticleFindAsync(ArticleFindOperation operation, bool reset = false)
    {
        if (_shuttingDown || !IsArticleFindVisible) return Task.CompletedTask;
        if (reset)
        {
            _findGeneration++;
            CancelPendingArticleFind();
            _findSessionActive = false;
            ArticleFindPrevious.IsEnabled = ArticleFindNext.IsEnabled = false;
            ArticleFindStatus.Text = ArticleFindText.Text.Length > 0 ? AppStrings.Get("FindSearching") : "";
        }
        _findCancellation ??= CancellationTokenSource.CreateLinkedTokenSource(_navigationCancellation.Token);
        var task = RunArticleFindAsync(_findTask, _findGeneration, ActiveNavigationID, operation,
            ArticleFindText.Text, ArticleFindMatchCase.IsChecked, ArticleFindWholeWord.IsChecked, _findCancellation.Token);
        _findTask = task;
        TrackBrowserTask(task);
        return task;
    }

    private async Task RunArticleFindAsync(Task previous, int generation, ulong navigation, ArticleFindOperation operation,
        string term, bool matchCase, bool wholeWord, CancellationToken cancellation)
    {
        await Task.Yield();
        try
        {
            await previous;
            cancellation.ThrowIfCancellationRequested();
            if (_shuttingDown || generation != _findGeneration || navigation != ActiveNavigationID || !IsArticleFindVisible) return;
            if (!_webViewReady || !ActiveDocumentReady || !HasDisplayedDocument)
            {
                ArticleFindStatus.Text = AppStrings.Get("FindLoadingArticle");
                return;
            }
            var find = GetArticleFind();
            if (term.Length == 0)
            {
                find.Stop();
                _findSessionActive = false;
                ArticleFindStatus.Text = "";
                ArticleFindPrevious.IsEnabled = ArticleFindNext.IsEnabled = false;
                return;
            }
            if (operation == ArticleFindOperation.Start || !_findSessionActive)
            {
                find.Stop();
                _findSessionActive = false;
                ArticleFindPrevious.IsEnabled = ArticleFindNext.IsEnabled = false;
                var options = ActiveWebView.CoreWebView2.Environment.CreateFindOptions();
                options.FindTerm = term;
                options.IsCaseSensitive = matchCase;
                options.ShouldMatchWord = wholeWord;
                options.ShouldHighlightAllMatches = true;
                options.SuppressDefaultFindDialog = true;
                var starting =
#if PARITY_PROBE
                    ProbeFindStart?.Invoke(options, cancellation) ??
#endif
                    find.StartAsync(options).AsTask(cancellation);
                await starting.WaitAsync(TimeSpan.FromSeconds(10), cancellation);
                if (_shuttingDown || generation != _findGeneration || navigation != ActiveNavigationID || !IsArticleFindVisible) return;
                _findSessionActive = true;
                _activeFindGeneration = generation;
                if (operation == ArticleFindOperation.Previous && find.MatchCount > 0) find.FindPrevious();
            }
            else if (operation == ArticleFindOperation.Previous) find.FindPrevious();
            else find.FindNext();
            UpdateArticleFindResults(find);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || _shuttingDown || generation != _findGeneration) { }
        catch (Exception error) when (IsArticleFindError(error))
        {
            if (generation == _findGeneration && navigation == ActiveNavigationID)
            {
                if (error is TimeoutException)
                {
                    StopArticleFind();
                    ReportArticleFindError(new TimeoutException(AppStrings.Get("FindTimedOut"), error));
                }
                else ReportArticleFindError(error);
            }
        }
    }

    private static bool IsArticleFindError(Exception error)
        => error is COMException or InvalidOperationException or ArgumentException or NotSupportedException
            or NotImplementedException or InvalidCastException or JsonException or OperationCanceledException or TimeoutException;

    private void ArticleFindResultChanged(CoreWebView2Find sender, object args)
    {
        if (_shuttingDown || !_findSessionActive || _activeFindGeneration != _findGeneration || !IsArticleFindVisible) return;
        try { UpdateArticleFindResults(sender); }
        catch (Exception error) when (IsArticleFindError(error)) { ReportArticleFindError(error); }
    }

    private void UpdateArticleFindResults(CoreWebView2Find find)
    {
        int count = find.MatchCount;
        ArticleFindPrevious.IsEnabled = ArticleFindNext.IsEnabled = count > 0;
        ArticleFindStatus.Text = count == 0 ? AppStrings.Get("NoFindMatches")
            : string.Format(CultureInfo.CurrentCulture, AppStrings.Get("FindMatchPosition"), find.ActiveMatchIndex, count);
    }

    private void ReportArticleFindError(Exception error)
    {
        App.LogException("Find in article", error);
        if (_shuttingDown) return;
        _findSessionActive = false;
        ArticleFindPrevious.IsEnabled = ArticleFindNext.IsEnabled = false;
        ArticleFindStatus.Text = string.Format(CultureInfo.CurrentCulture, AppStrings.Get("ArticleFindError"), error.Message);
        ViewModel.StatusMessage = ArticleFindStatus.Text;
    }

    private async void ArticleFindText_TextChanged(object sender, TextChangedEventArgs args)
        => await QueueArticleFindAsync(ArticleFindOperation.Start, reset: true);
    private async void ArticleFindOption_Click(object sender, RoutedEventArgs args)
        => await QueueArticleFindAsync(ArticleFindOperation.Start, reset: true);
    private async void ArticleFindPrevious_Click(object sender, RoutedEventArgs args) => await FindNextInArticleAsync(backwards: true);
    private async void ArticleFindNext_Click(object sender, RoutedEventArgs args) => await FindNextInArticleAsync();
    private void ArticleFindClose_Click(object sender, RoutedEventArgs args) => HideArticleFind();
    private async void ArticleFindText_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == Windows.System.VirtualKey.Escape)
        {
            args.Handled = true;
            HideArticleFind();
        }
        else if (args.Key == Windows.System.VirtualKey.Enter)
        {
            args.Handled = true;
            bool backwards = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
            await FindNextInArticleAsync(backwards);
        }
    }
}
