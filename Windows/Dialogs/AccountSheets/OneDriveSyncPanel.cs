using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinNewsWire.Account.OneDrive;
using WinNewsWire.AppRuntime;

namespace WinNewsWire.Dialogs.AccountSheets;

internal sealed class OneDriveSyncPanel : StackPanel
{
    private readonly Account.Account _account;
    private readonly TextBlock _results = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _phase = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressRing _progress = new() { Width = 24, Height = 24, IsActive = false };
    private readonly Button _scan = new() { Content = "Scan" };
    private readonly Button _clean = new() { Content = "Clean Up", IsEnabled = false };
    private readonly Button _cancel = new() { Content = "Cancel", IsEnabled = false };
    private readonly Button _copy = new() { Content = "Copy Report", IsEnabled = false };
    private readonly CheckBox _confirm = new() { IsEnabled = false };
    private CancellationTokenSource? _cancellation;
    private OneDriveSyncStatistics? _statistics;
    private DateTime _scanDate;
    private bool _stale;
    private string? _report;
    private int _generation;

    internal OneDriveSyncPanel(Account.Account account)
    {
        _account = account;
        Spacing = 8;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var control in new Control[] { _scan, _clean, _cancel, _copy, _progress }) actions.Children.Add(control);
        Children.Add(_results);
        Children.Add(_phase);
        Children.Add(actions);
        _confirm.Content = new TextBlock
        {
            Text = "I confirm cleanup of unneeded synced article bodies. Starred content and local history are kept. Cleanup may take several minutes.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 480
        };
        Children.Add(_confirm);
        Children.Add(new TextBlock
        {
            Text = "To reduce sync storage, turn off unread-content sync and clean up unneeded bodies. Keep your PCs up to date so they receive cleanup. OneDrive version history may still retain older copies.",
            TextWrapping = TextWrapping.Wrap
        });
        _scan.Click += async (_, _) => await RunAsync(cleanup: false);
        _clean.Click += async (_, _) => await RunAsync(cleanup: true);
        _cancel.Click += (_, _) => { _phase.Text = "Canceling..."; _cancellation?.Cancel(); };
        _copy.Click += (_, _) => CopyReport();
        _confirm.Checked += (_, _) => UpdateButtons();
        _confirm.Unchecked += (_, _) => UpdateButtons();
        Unloaded += (_, _) => _cancellation?.Cancel();
    }

    private void UpdateButtons()
    {
        bool busy = _cancellation is not null;
        _scan.IsEnabled = !busy;
        _cancel.IsEnabled = busy;
        _copy.IsEnabled = !busy && _report is not null;
        _confirm.IsEnabled = !busy && _statistics is not null;
        _clean.IsEnabled = !busy && _confirm.IsChecked == true && _statistics is not null
            && (_statistics.EligibleContent > 0 || _stale);
    }

    private async Task RunAsync(bool cleanup)
    {
        if (_cancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        int generation = ++_generation;
        _progress.IsActive = true;
        _phase.Text = cleanup
            ? _stale || DateTime.UtcNow - _scanDate > TimeSpan.FromMinutes(5)
                ? "The old plan is stale. Rechecking read/orphaned and disabled-unread content before cleanup..."
                : $"Confirming cleanup of up to {_statistics!.EligibleContent:N0} unneeded article bodies..."
            : "Scanning OneDrive. Counts are incomplete until the scan finishes.";
        UpdateButtons();
        var progress = new Progress<OneDriveSyncProgress>(value =>
        {
            if (_cancellation is not null && generation == _generation)
                _phase.Text = value.Total > 0 ? $"{value.Phase}: {value.Completed:N0} / {value.Total:N0}" : value.Phase;
        });
        try
        {
            if (cleanup)
            {
                var result = await _account.CleanUpOneDriveContentAsync(cancellation.Token, progress);
                _stale = true;
                _report = result + "\nPrevious scan results are now stale. Scan again for authoritative counts.";
                _results.Text = _report + "\n\nPrevious scan (stale):\n" + _statistics;
                _phase.Text = "Cleanup completed. Local article history was not removed.";
            }
            else
            {
                _statistics = await _account.ScanOneDriveAsync(progress, cancellation.Token);
                _scanDate = DateTime.UtcNow;
                _stale = false;
                _report = $"OneDrive scan: {_scanDate.ToLocalTime():g}\n{_statistics}";
                _results.Text = _report;
                _phase.Text = "Scan completed.";
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _phase.Text = cleanup
                ? "Cleanup canceled. Some changes may already have been applied. Scan again before retrying."
                : "Scan canceled. Displayed old counts are stale; no complete cleanup plan is available.";
            _statistics = null;
            _stale = true;
        }
        catch (Exception error) when (AccountSheetHelpers.IsSetupError(error) || error is System.IO.InvalidDataException or FormatException)
        {
            _phase.Text = "Operation failed: " + error.Message
                + "\nCounts are incomplete or stale. Some changes may already have succeeded; scan again before retrying.";
            _statistics = null;
            _stale = true;
            await AppService.Shared.LogAsync("OneDrive sync maintenance", error);
        }
        finally
        {
            ++_generation;
            _cancellation = null;
            _progress.IsActive = false;
            _confirm.IsChecked = false;
            UpdateButtons();
        }
    }

    private void CopyReport()
    {
        if (_report is null) return;
        try
        {
            var data = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText(_report);
            global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            _phase.Text = "Report copied. Paste it into the app you want to share it with.";
        }
        catch (System.Runtime.InteropServices.COMException error)
        {
            _phase.Text = "Could not copy the report: " + error.Message;
            App.LogException("Copy OneDrive report", error);
        }
    }
}
