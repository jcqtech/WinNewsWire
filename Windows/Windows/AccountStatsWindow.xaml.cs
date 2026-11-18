using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinNewsWire.Account;
using WinNewsWire.AppRuntime;
using WinNewsWire.AppShared.AccountStats;
using WinNewsWire.Core;
using ActivityLogger = WinNewsWire.ActivityLog.ActivityLog;

namespace WinNewsWire.AppWindows;

public sealed partial class AccountStatsWindow : Window
{
    private const string OpenStateKey = "AccountStatsWindowIsOpen";
    private const string ColumnWidthsKey = "AccountStatsColumnWidths";
    private static AccountStatsWindow? _current;
    private readonly AccountStatsViewModel _model = new(AppService.Shared.Accounts);
    private readonly HashSet<Account.Account> _observed = [];
    private readonly CancellationTokenSource _lifetime = new();
    private Task _refresh = Task.CompletedTask;
    private bool _refreshAgain;
    private bool _vacuuming;
    private bool _closed;
    private bool _columnsInitialized;
    private ColumnDefinition[] _columns = [];

    public static bool ShouldOpenAtStartup => AppDefaults.Shared.GetValue(OpenStateKey, false);
    public static AccountStatsWindow Show()
    {
        _current ??= new();
        _current.Activate();
        return _current;
    }

    public sealed class Row(string name, bool active, bool total, AccountStatsTotals values, IReadOnlyList<ColumnDefinition> columns)
    {
        public IReadOnlyList<ColumnDefinition> Columns => columns;
        public string Name => name;
        public double Opacity => active ? 1 : 0.6;
        public global::Windows.UI.Text.FontWeight Weight => total ? FontWeights.Bold : FontWeights.Normal;
        public string DatabaseSize => ActivityLogger.DataSizeMessage(values.DatabaseSizeBytes);
        public string Feeds => Number(values.FeedCount);
        public string Folders => Number(values.FolderCount);
        public string Articles => Number(values.ArticleCount);
        public string Statuses => Number(values.StatusesCount);
        public string Unread => Number(values.UnreadCount);
        public string Starred => Number(values.StarredCount);
        private static string Number(long count) => count.ToString("N0", CultureInfo.CurrentCulture);
    }

    private AccountStatsWindow()
    {
        InitializeComponent();
        _columns = StatsHeader.ColumnDefinitions.ToArray();
        WindowIconHelper.ApplyFlatIcon(this);
        WindowThemeHelper.Attach(this);
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32(1140, 540));
        AppService.Shared.Accounts.AccountsDidChange += AccountChanged;
        ObserveAccounts();
        async void OnActivated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated) await RefreshAsync();
        }
        void OnClosed(object sender, WindowEventArgs args)
        {
            Closed -= OnClosed;
            Activated -= OnActivated;
            _closed = true;
            _lifetime.Cancel();
            _ = _refresh.ContinueWith(_ => _lifetime.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            AppService.Shared.Accounts.AccountsDidChange -= AccountChanged;
            foreach (var account in _observed) Unobserve(account);
            _observed.Clear();
            if (!App.IsShuttingDown) SaveOpenState(false);
            _current = null;
        }
        Activated += OnActivated;
        Closed += OnClosed;
        SaveOpenState(true);
    }

    private void StatsHeader_Loaded(object sender, RoutedEventArgs args)
    {
        if (_columnsInitialized) return;
        _columnsInitialized = true;
        var saved = AppDefaults.Shared.GetValue<Dictionary<string, double>>(ColumnWidthsKey) ?? new();
        foreach (var button in StatsHeader.Children.OfType<Button>())
        {
            var column = _columns[Grid.GetColumn(button)];
            var label = new TextBlock { Text = (string)button.Content, FontSize = button.FontSize, FontFamily = button.FontFamily };
            label.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            column.MinWidth = Math.Max(column.MinWidth, Math.Ceiling(label.DesiredSize.Width) + 28);
            double width = column.Width.Value < column.MinWidth ? column.MinWidth + 28 : column.Width.Value;
            if (saved.TryGetValue((string)button.Tag, out var stored))
            {
                if (!double.IsFinite(stored))
                    System.Diagnostics.Trace.TraceError($"The saved statistics column width for {button.Tag} is invalid.");
                else width = stored;
            }
            column.Width = new GridLength(Math.Clamp(width, column.MinWidth, column.MaxWidth));
        }
        UpdateTableWidth();
    }

    private void UpdateTableWidth()
        => StatsList.Width = _columns.Sum(column => column.Width.Value) + StatsHeader.Padding.Left + StatsHeader.Padding.Right
            + StatsHeader.ColumnSpacing * Math.Max(0, _columns.Length - 1);

    private void Column_WidthChanged(object? sender, EventArgs args) => UpdateTableWidth();

    private void Column_ResizeCompleted(object? sender, EventArgs args)
    {
        var widths = StatsHeader.Children.OfType<Button>().ToDictionary(button => (string)button.Tag,
            button => _columns[Grid.GetColumn(button)].Width.Value, StringComparer.Ordinal);
        try { AppDefaults.Shared.SetValue(ColumnWidthsKey, widths); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Could not save statistics column widths: " + error.Message;
            App.LogException("Save statistics columns", error);
        }
    }

    private static void SaveOpenState(bool open)
    {
        try { AppDefaults.Shared.SetValue(OpenStateKey, open); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceError($"Could not save the account-statistics window state: {error}");
        }
    }

    private void ObserveAccounts()
    {
        var accounts = AppService.Shared.Accounts.Accounts.ToHashSet();
        foreach (var account in _observed.Except(accounts).ToArray())
        {
            Unobserve(account);
            _observed.Remove(account);
        }
        foreach (var account in accounts.Except(_observed))
        {
            account.AccountStructureChanged += AccountChanged;
            account.ArticlesDeleted += AccountChanged;
            account.StatusesChanged += AccountChanged;
            account.NewArticlesDownloaded += AccountChanged;
            _observed.Add(account);
        }
    }

    private void Unobserve(Account.Account account)
    {
        account.AccountStructureChanged -= AccountChanged;
        account.ArticlesDeleted -= AccountChanged;
        account.StatusesChanged -= AccountChanged;
        account.NewArticlesDownloaded -= AccountChanged;
    }

    private void AccountChanged(object? sender, EventArgs args)
    {
        if (_closed) return;
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            if (_closed) return;
            ObserveAccounts();
            await RefreshAsync();
        })) System.Diagnostics.Trace.TraceWarning("Account statistics could not schedule a refresh.");
    }

    internal Task RefreshAsync()
    {
        if (_closed) return Task.CompletedTask;
        _refreshAgain = true;
        if (_refresh.IsCompleted) _refresh = RefreshCoreAsync();
        return _refresh;
    }

    private async Task RefreshCoreAsync()
    {
        WorkProgress.IsActive = true;
        RefreshButton.IsEnabled = false;
        try
        {
            while (_refreshAgain && !_closed)
            {
                _refreshAgain = false;
                await _model.RefreshAsync(_lifetime.Token);
                if (!_closed) RenderRows();
            }
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException)
        {
            if (!_closed) StatusText.Text = "Could not refresh account statistics: " + error.Message;
            await AppService.Shared.LogAsync("Account statistics", error);
        }
        finally
        {
            if (!_closed)
            {
                WorkProgress.IsActive = _vacuuming;
                RefreshButton.IsEnabled = true;
            }
        }
    }

    private void RenderRows()
    {
        var rows = _model.SortedAccountStats.Select(row => new Row(
            row.Name == row.TypeName ? row.Name : $"{row.Name} ({row.TypeName})", row.IsActive, false,
            new(row.FeedCount, row.FolderCount, row.ArticleCount, row.StatusesCount, row.UnreadCount, row.StarredCount, row.DatabaseSizeBytes), _columns)).ToList();
        if (rows.Count > 1) rows.Add(new("Totals", true, true, _model.Totals, _columns));
        StatsList.ItemsSource = rows;
        StatusText.Text = $"{_model.SortedAccountStats.Count} accounts. Sorted by {_model.SortKey} ({(_model.Descending ? "descending" : "ascending")}).";
    }

    private void Sort_Click(object sender, RoutedEventArgs args)
    {
        var key = Enum.Parse<AccountStatsSortKey>((string)((Button)sender).Tag);
        _model.Descending = _model.SortKey == key && !_model.Descending;
        _model.SortKey = key;
        _model.ApplySort();
        RenderRows();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs args) => await RefreshAsync();
    private void Close_Click(object sender, RoutedEventArgs args) => Close();

    private async void Vacuum_Click(object sender, RoutedEventArgs args)
    {
        if (_vacuuming) return;
        _vacuuming = true;
        VacuumButton.IsEnabled = false;
        WorkProgress.IsActive = true;
        StatusText.Text = "Compacting databases...";
        try
        {
            await AppService.Shared.VacuumAllDatabasesAsync();
            await RefreshAsync();
            if (!_closed) StatusText.Text = "Vacuum completed. Subscriptions and article history were preserved.";
        }
        catch (OperationCanceledException) when (App.IsShuttingDown) { }
        catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException
            or InvalidOperationException or AggregateException)
        {
            if (!_closed) StatusText.Text = "Could not vacuum databases: " + error.Message;
            await AppService.Shared.LogAsync("Vacuum databases", error);
        }
        finally
        {
            _vacuuming = false;
            if (!_closed)
            {
                VacuumButton.IsEnabled = true;
                WorkProgress.IsActive = !_refresh.IsCompleted;
            }
        }
    }
}
