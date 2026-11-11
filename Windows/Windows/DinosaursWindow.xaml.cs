using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinNewsWire.Account;
using WinNewsWire.AppRuntime;
using WinNewsWire.AppShared;
using WinNewsWire.AppShared.Dinosaurs;
using WinNewsWire.Core;
using WinRT.Interop;

namespace WinNewsWire.AppWindows;

public sealed partial class DinosaursWindow : Window, IAsyncWindowLifetime
{
#if PARITY_PROBE
    internal Action<global::Windows.ApplicationModel.DataTransfer.DataPackage>? ProbeClipboardWriter { get; set; }
#endif
    private const string OpenKey = "DinosaurWindowIsOpen";
    private const string MonthsKey = "DinosaurMonthThreshold";
    private const string WidthsKey = "DinosaurColumnWidths";
    private static DinosaursWindow? _current;
    private readonly DinosaursViewModel _model = new(AppService.Shared.Accounts);
    private readonly UndoManager _history = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Account.Account> _observed = [];
    private ColumnDefinition[] _columns = [];
    private Task _refresh = Task.CompletedTask;
    private Task _action = Task.CompletedTask;
    private Task? _shutdown;
    private ContentDialog? _confirmation;
    private bool _refreshAgain, _busy, _closing, _closed, _updatingThreshold, _initialized;
    private double _accountWidth = 180;

    public static bool ShouldOpenAtStartup => AppDefaults.Shared.GetValue(OpenKey, false);
    public static DinosaursWindow Show()
    {
        _current ??= new();
        _current.Activate();
        return _current;
    }

    public sealed record Row(DinosaurRow Data, IReadOnlyList<ColumnDefinition> Columns, bool ShowAccount)
    {
        public Visibility AccountVisibility => ShowAccount ? Visibility.Visible : Visibility.Collapsed;
        public string LastArticle => Data.LastArticleDate?.ToLocalTime().ToString("d", CultureInfo.CurrentCulture) ?? "";
        public string LastResponse => Data.LastResponseCode?.ToString(CultureInfo.CurrentCulture) ?? "";
    }

    private DinosaursWindow()
    {
        InitializeComponent();
        _columns = TableHeader.ColumnDefinitions.ToArray();
        WindowIconHelper.ApplyFlatIcon(this);
        WindowThemeHelper.Attach(this);
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32(1140, 590));
        ThresholdLabel.Text = AppStrings.Get("DinosaurThreshold");
        MonthsLabel.Text = AppStrings.Get("Months");
        AutomationProperties.SetName(MonthThreshold, AppStrings.Get("Months"));
        AutomationProperties.SetName(FeedsList, AppStrings.Get("StaleFeeds"));
        var headers = new[] { FeedHeader, UrlHeader, AccountHeader, DateHeader, ResponseHeader };
        var labels = new[] { "Feed", "FeedUrl", "Account", "LastArticle", "LastResponse" };
        for (int index = 0; index < headers.Length; index++)
        {
            headers[index].Content = AppStrings.Get(labels[index]);
            var thumb = TableHeader.Children.OfType<Helpers.ColumnResizeThumb>().Single(item => Grid.GetColumn(item) == index);
            AutomationProperties.SetName(thumb, string.Format(CultureInfo.CurrentCulture, AppStrings.Get("ResizeNamedColumn"), headers[index].Content));
        }
        SelectFeedButton.Content = AppStrings.Get("SelectInSidebar");
        DeleteButton.Content = AppStrings.Get("Delete");
        HomePageButton.Content = AppStrings.Get("OpenHomePage");
        CopyButton.Content = AppStrings.Get("CopyFeedUrl");
        UndoButton.Content = AppStrings.Get("Undo");
        RedoButton.Content = AppStrings.Get("Redo");
        RefreshButton.Content = AppStrings.Get("Refresh");
        HelpButton.Content = AppStrings.Get("Help");
        CloseButton.Content = AppStrings.Get("Close");
        var saved = AppDefaults.Shared.GetValue<double[]>(WidthsKey);
        if (saved?.Length == _columns.Length)
            for (int index = 0; index < saved.Length; index++)
                if (double.IsFinite(saved[index])) _columns[index].Width = new(Math.Clamp(saved[index], Math.Max(120, _columns[index].MinWidth), 1000));
        _accountWidth = AccountColumn.Width.Value;
        int months = AppDefaults.Shared.GetValue(MonthsKey, 6);
        _model.MonthThreshold = months == 0 ? 6 : Math.Clamp(months, 1, 350);
        _updatingThreshold = true;
        MonthThreshold.Value = _model.MonthThreshold;
        _updatingThreshold = false;
        _model.SortBy(DinosaurSortKey.LastArticleDate, ascending: false);
        _history.StackChanged += HistoryChanged;
        AppService.Shared.Accounts.AccountsDidChange += AccountsChanged;
        ObserveAccounts();
        async void OnActivated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated) await RefreshAsync();
        }
        async void OnClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
        {
            if (_closing) return;
            args.Cancel = true;
            await PrepareToCloseAsync();
            Close();
        }
        void OnClosed(object sender, WindowEventArgs args)
        {
            Closed -= OnClosed;
            Activated -= OnActivated;
            AppWindow.Closing -= OnClosing;
            _closed = true;
            _closing = true;
            _lifetime.Cancel();
            AppService.Shared.Accounts.AccountsDidChange -= AccountsChanged;
            foreach (var account in _observed) Unobserve(account);
            _history.StackChanged -= HistoryChanged;
            _ = Task.WhenAll(_refresh, _action).ContinueWith(_ => _lifetime.Dispose(), TaskScheduler.Default);
            if (!App.IsShuttingDown) SaveOpenState(false);
            _current = null;
        }
        Activated += OnActivated;
        AppWindow.Closing += OnClosing;
        Closed += OnClosed;
        SaveOpenState(true);
        _initialized = true;
        UpdateTableWidth();
    }

    private void SaveOpenState(bool open)
    {
        try { AppDefaults.Shared.SetValue(OpenKey, open); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Report("Save Dinosaurs state", error); }
    }

    public Task PrepareToCloseAsync() => _shutdown ??= StopAsync();
    private async Task StopAsync()
    {
        _closing = true;
        _confirmation?.Hide();
        _lifetime.Cancel();
        await Task.WhenAll(_refresh, _action);
    }

    private void ObserveAccounts()
    {
        var current = AppService.Shared.Accounts.Accounts.ToHashSet();
        foreach (var account in _observed.Except(current).ToArray()) { Unobserve(account); _observed.Remove(account); }
        foreach (var account in current.Except(_observed))
        {
            account.AccountStructureChanged += AccountsChanged;
            account.NewArticlesDownloaded += AccountsChanged;
            _observed.Add(account);
        }
    }

    private void Unobserve(Account.Account account)
    {
        account.AccountStructureChanged -= AccountsChanged;
        account.NewArticlesDownloaded -= AccountsChanged;
    }

    private void AccountsChanged(object? sender, EventArgs args)
    {
        if (_closing) return;
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            if (_closing) return;
            ObserveAccounts();
            await RefreshAsync();
        })) App.LogException("Refresh Dinosaurs", null, "The window dispatcher is unavailable.");
    }

    internal Task RefreshAsync()
    {
        if (_closing) return Task.CompletedTask;
        _refreshAgain = true;
        if (!_busy && _refresh.IsCompleted) _refresh = RefreshCoreAsync();
        return _refresh;
    }

    private async Task RefreshCoreAsync()
    {
        await Task.Yield();
        if (_closing) return;
        try
        {
            WorkProgress.IsActive = true;
            while (_refreshAgain && !_closing)
            {
                _refreshAgain = false;
                if (double.IsNaN(MonthThreshold.Value)) _model.Clear();
                else await _model.RefreshAsync(_lifetime.Token);
                if (!_closing) RenderRows();
            }
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception error) when (IsExpectedError(error)) { Report("Refresh Dinosaurs", error); }
        finally { if (!_closing) { WorkProgress.IsActive = _busy; UpdateButtons(); } }
    }

    private void RenderRows()
    {
        var selected = SelectedRows().Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        AccountColumn.Width = new(_model.ShowAccountColumn ? _accountWidth : 0);
        AccountHeader.Visibility = AccountResize.Visibility = _model.ShowAccountColumn ? Visibility.Visible : Visibility.Collapsed;
        var rows = _model.Rows.Select(row => new Row(row, _columns, _model.ShowAccountColumn)).ToArray();
        FeedsList.ItemsSource = rows;
        foreach (var row in rows.Where(row => selected.Contains(row.Data.Id))) FeedsList.SelectedItems.Add(row);
        StatusText.Text = string.Format(CultureInfo.CurrentCulture, AppStrings.Get("DinosaurCount"), rows.Length);
        UpdateTableWidth();
        UpdateButtons();
    }

    private DinosaurRow[] SelectedRows() => FeedsList.SelectedItems.OfType<Row>().Select(row => row.Data).ToArray();
    private void UpdateButtons()
    {
        int count = FeedsList.SelectedItems.Count;
        SelectFeedButton.IsEnabled = !_busy && count == 1;
        DeleteButton.IsEnabled = HomePageButton.IsEnabled = CopyButton.IsEnabled = !_busy && count > 0;
        UndoButton.IsEnabled = !_busy && _history.CanUndo;
        RedoButton.IsEnabled = !_busy && _history.CanRedo;
        RefreshButton.IsEnabled = MonthThreshold.IsEnabled = !_busy;
    }
    private void HistoryChanged(object? sender, EventArgs args) { if (!_closing) UpdateButtons(); }
    private void FeedsList_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_initialized && !_closed) UpdateButtons();
    }

    private async void MonthThreshold_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_initialized || _updatingThreshold || _closing) return;
        if (double.IsNaN(args.NewValue)) { _model.Clear(); RenderRows(); return; }
        int value = (int)Math.Clamp(Math.Truncate(args.NewValue), 1, 350);
        try
        {
            AppDefaults.Shared.SetValue(MonthsKey, value);
            _model.MonthThreshold = value;
            _updatingThreshold = true;
            MonthThreshold.Value = value;
            _updatingThreshold = false;
            await RefreshAsync();
        }
        catch (Exception error) when (IsExpectedError(error))
        {
            _updatingThreshold = true;
            MonthThreshold.Value = _model.MonthThreshold;
            _updatingThreshold = false;
            Report("Save stale-feed threshold", error);
        }
    }

    private void Sort_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse<DinosaurSortKey>(tag, out var key)) return;
        _model.SortBy(key, _model.SortKey != key || !_model.Ascending);
        RenderRows();
    }
    private void UpdateTableWidth()
    {
        if (_columns.Length > 0 && FeedsList is not null)
            FeedsList.Width = _columns.Sum(column => column.Width.Value) + 56;
    }
    private void Column_WidthChanged(object? sender, EventArgs args) => UpdateTableWidth();
    private void Column_ResizeCompleted(object? sender, EventArgs args)
    {
        if (_model.ShowAccountColumn) _accountWidth = AccountColumn.Width.Value;
        var widths = _columns.Select(column => column.Width.Value).ToArray();
        widths[2] = _accountWidth;
        try { AppDefaults.Shared.SetValue(WidthsKey, widths); }
        catch (Exception error) when (IsExpectedError(error)) { Report("Save Dinosaurs columns", error); }
    }

    private async void Delete_Click(object sender, RoutedEventArgs args)
    {
        var rows = SelectedRows();
        if (_busy || rows.Length == 0) return;
        _confirmation = new ContentDialog
        {
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
            Title = AppStrings.Get("DeleteFeeds"),
            Content = string.Format(CultureInfo.CurrentCulture, AppStrings.Get("DeleteStaleFeedsConfirmation"), rows.Length),
            PrimaryButtonText = AppStrings.Get("Delete"), CloseButtonText = AppStrings.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        WindowThemeHelper.Apply(_confirmation);
        var result = await _confirmation.ShowAsync();
        _confirmation = null;
        if (_closing || result != ContentDialogResult.Primary) return;
        await RunActionAsync("Delete stale feeds", async () =>
        {
            if (_model.CreateDeletionCommand(rows) is { } command) await _history.ExecuteAsync(command);
        }, refreshAfter: true);
    }

    private Task RunActionAsync(string operation, Func<Task> work, bool refreshAfter = false)
    {
        if (_busy || _closing) return Task.CompletedTask;
        return _action = RunActionCoreAsync(operation, work, refreshAfter);
    }
    private async Task RunActionCoreAsync(string operation, Func<Task> work, bool refreshAfter)
    {
        _busy = true;
        UpdateButtons();
        await Task.Yield();
        Exception? failure = null;
        try
        {
            await _refresh;
            if (!_closing) await work();
        }
        catch (Exception error) when (IsExpectedError(error)) { failure = error; }
        finally
        {
            _busy = false;
            if (!_closing)
            {
                UpdateButtons();
                if (refreshAfter || _refreshAgain) await RefreshAsync();
            }
        }
        if (failure is not null) Report(operation, failure);
    }

    private async void SelectFeed_Click(object sender, RoutedEventArgs args)
    {
        if (SelectedRows() is not [var row]) return;
        await RunActionAsync("Select feed", async () =>
        {
            var main = App.MainWindows.FirstOrDefault() ?? App.OpenMainWindow();
            await main.RevealFeedAsync(row.Account.AccountID, row.Feed.FeedID);
        });
    }
    private async void HomePage_Click(object sender, RoutedEventArgs args)
    {
        var rows = SelectedRows();
        await RunActionAsync("Open feed website", async () =>
        {
            bool usedFallback = false;
            foreach (var row in rows)
            {
                Uri? url = null;
                if (!Uri.TryCreate(row.Feed.HomePageUrl, UriKind.Absolute, out url) || url.Scheme is not ("http" or "https"))
                {
                    if (!Uri.TryCreate(row.FeedUrl, UriKind.Absolute, out var feedUrl) || feedUrl.Scheme is not ("http" or "https"))
                        throw new InvalidOperationException("The feed has no supported website URL.");
                    url = new Uri(feedUrl.GetLeftPart(UriPartial.Authority) + "/");
                }
                usedFallback |= await Browser.OpenAsync(url, WindowNative.GetWindowHandle(this));
            }
            if (usedFallback) throw new InvalidOperationException("The selected browser is unavailable; Windows opened its default browser instead.");
        });
    }
    private void Copy_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var rows = SelectedRows();
            if (rows.Length == 0) return;
            var data = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText(string.Join(Environment.NewLine, rows.Select(row => row.FeedUrl)));
#if PARITY_PROBE
            if (ProbeClipboardWriter is { } copy)
            {
                copy(data);
                return;
            }
#endif
            global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
        }
        catch (Exception error) when (IsExpectedError(error)) { Report("Copy feed URLs", error); }
    }
    private async void FeedIcon_Loaded(object sender, RoutedEventArgs args)
    {
        if (_closing || sender is not Helpers.IconView { DataContext: Row row } icon) return;
        try
        {
            var path = await AppShared.Favicons.FaviconDownloader.Shared.FaviconPathAsync(row.Data.Feed).WaitAsync(_lifetime.Token);
            if (!_closing && ReferenceEquals(icon.DataContext, row))
                icon.ImageSource = path is null ? null : new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path));
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception error) when (IsExpectedError(error)) { App.LogException("Load stale-feed icon", error); }
    }
    private async void Help_Click(object sender, RoutedEventArgs args) => await RunActionAsync("Open Dinosaurs help", async () =>
    {
        if (!await global::Windows.System.Launcher.LaunchUriAsync(new Uri("https://netnewswire.com/help/dinosaurs.html")))
            throw new InvalidOperationException("Windows could not open the Dinosaurs help page.");
    });
    private async void Undo_Click(object sender, RoutedEventArgs args) => await RunActionAsync("Undo feed deletion", _history.UndoAsync, refreshAfter: true);
    private async void Redo_Click(object sender, RoutedEventArgs args) => await RunActionAsync("Redo feed deletion", _history.RedoAsync, refreshAfter: true);
    private async void Refresh_Click(object sender, RoutedEventArgs args) => await RefreshAsync();
    private async void Close_Click(object sender, RoutedEventArgs args) { await PrepareToCloseAsync(); Close(); }
    private bool IsEditingThreshold => FocusManager.GetFocusedElement(((FrameworkElement)Content).XamlRoot) is TextBox;
    private void Delete_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsEditingThreshold || !DeleteButton.IsEnabled) return;
        args.Handled = true;
        Delete_Click(DeleteButton, new RoutedEventArgs());
    }
    private async void Undo_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsEditingThreshold || !_history.CanUndo) return;
        args.Handled = true;
        await RunActionAsync("Undo feed deletion", _history.UndoAsync, refreshAfter: true);
    }
    private async void Redo_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsEditingThreshold || !_history.CanRedo) return;
        args.Handled = true;
        await RunActionAsync("Redo feed deletion", _history.RedoAsync, refreshAfter: true);
    }
    private static bool IsExpectedError(Exception error) => error is IOException or UnauthorizedAccessException
        or InvalidOperationException or ArgumentException or AggregateException or System.Net.Http.HttpRequestException
        or Microsoft.Data.Sqlite.SqliteException or System.Runtime.InteropServices.COMException or OperationCanceledException;
    private void Report(string operation, Exception error)
    {
        App.LogException(operation, error);
        if (!_closed) StatusText.Text = operation + ": " + error.Message;
    }
}
