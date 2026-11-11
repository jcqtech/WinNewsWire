using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinNewsWire.ActivityLog;
using ActivityLogger = WinNewsWire.ActivityLog.ActivityLog;

namespace WinNewsWire.AppWindows;

public sealed partial class ActivityLogWindow : Window
{
    public sealed class Row(ActivitySnapshot activity)
    {
        public ActivitySnapshot Activity { get; } = activity;
        public string State => Activity.State.ToString();
        public string Header => Activity.Owner.DisplayName + " - " + Activity.Kind.DisplayName(Activity.Detail);
        public string Duration => Activity.FormattedDuration ?? "";
        public string Message => Activity.Error?.Message ?? Activity.CompletionMessage ?? Activity.Detail ?? "";
        public string ClipboardText => $"{State}\t{Header}\t{Duration}{Environment.NewLine}{Message}";
    }

    private readonly ActivityLogger _log = ActivityLogger.Shared;
    private List<Row> _visible = [];
    private IReadOnlyList<ActivityOwner> _owners = [];
    private int _updateQueued;
    private bool _updatingFilters;
    private volatile bool _closed;

    public ActivityLogWindow()
    {
        InitializeComponent();
        WindowIconHelper.ApplyFlatIcon(this);
        WindowThemeHelper.Attach(this);
        _log.ActivityDidChange += OnActivityChanged;
        void OnClosed(object sender, WindowEventArgs args)
        {
            Closed -= OnClosed;
            _closed = true;
            _log.ActivityDidChange -= OnActivityChanged;
        }
        Closed += OnClosed;
        RefreshRows();
    }

    private void OnActivityChanged(object? sender, EventArgs args)
    {
        if (_closed || Interlocked.Exchange(ref _updateQueued, 1) != 0) return;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _updateQueued, 0);
            if (!_closed) RefreshRows();
        })) Interlocked.Exchange(ref _updateQueued, 0);
    }

    private void RefreshRows()
    {
        if (_closed || _updatingFilters || ActivitiesList is null) return;
        var snapshot = _log.Snapshot();
        var activities = snapshot.Pending.Concat(snapshot.Running).Concat(snapshot.Completed).ToList();
        var selectedOwner = (OwnerFilter.SelectedItem as ComboBoxItem)?.Tag as ActivityOwner;
        var owners = activities.GroupBy(activity => activity.Owner).Select(group => group.MaxBy(activity => activity.ID)!.Owner)
            .OrderBy(owner => owner.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
        if (!owners.Select(owner => (owner, owner.DisplayName)).SequenceEqual(_owners.Select(owner => (owner, owner.DisplayName))))
        {
            _updatingFilters = true;
            OwnerFilter.Items.Clear();
            OwnerFilter.Items.Add(new ComboBoxItem { Content = "All activities" });
            foreach (var owner in owners) OwnerFilter.Items.Add(new ComboBoxItem { Content = owner.DisplayName, Tag = owner });
            OwnerFilter.SelectedItem = OwnerFilter.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, selectedOwner))
                ?? OwnerFilter.Items[0];
            _owners = owners;
            _updatingFilters = false;
        }
        else if (OwnerFilter.Items.Count == 0)
        {
            _updatingFilters = true;
            OwnerFilter.Items.Add(new ComboBoxItem { Content = "All activities" });
            OwnerFilter.SelectedIndex = 0;
            _updatingFilters = false;
        }
        selectedOwner = (OwnerFilter.SelectedItem as ComboBoxItem)?.Tag as ActivityOwner;
        var selectedIDs = ActivitiesList.SelectedItems.OfType<Row>().Select(row => row.Activity.ID).ToHashSet();
        var query = SearchBox.Text?.Trim() ?? "";
        _visible = activities.Where(activity => selectedOwner is null || activity.Owner.Equals(selectedOwner))
            .Where(activity => ShowCompleted.IsChecked == true || activity.State is ActivityState.Pending or ActivityState.Running)
            .OrderBy(activity => activity.State is ActivityState.Running ? 0 : activity.State is ActivityState.Pending ? 1 : 2)
            .ThenByDescending(activity => activity.ID).Select(activity => new Row(activity))
            .Where(row => query.Length == 0 || row.Header.Contains(query, StringComparison.OrdinalIgnoreCase) || row.Message.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        ActivitiesList.ItemsSource = _visible;
        foreach (var row in _visible.Where(row => selectedIDs.Contains(row.Activity.ID))) ActivitiesList.SelectedItems.Add(row);
        StatusText.Text = $"{_log.RunningActivities.Count} running, {_log.PendingActivities.Count} pending, {_visible.Count} shown";
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => RefreshRows();
    private void Search_Changed(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => RefreshRows();
    private void CompletedFilter_Changed(object sender, RoutedEventArgs e) => RefreshRows();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        var selected = ActivitiesList.SelectedItems.OfType<Row>().ToList();
        if (selected.Count == 0) selected = _visible;
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(string.Join(Environment.NewLine + Environment.NewLine, selected.Select(row => row.ClipboardText)));
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
        catch (System.Runtime.InteropServices.COMException error)
        {
            StatusText.Text = "Could not copy activities: " + error.Message;
            await AppRuntime.AppService.Shared.LogAsync("Copy activities", error);
        }
    }
}
