using System;
using System.IO;
using System.Linq;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using WinNewsWire.ActivityLog;
using WinNewsWire.AppShared;
using WinNewsWire.AppShared.CurrentActivity;
using WinNewsWire.Core;

namespace WinNewsWire.AppWindows;

public sealed partial class CurrentActivityWindow : Window
{
    private const string OpenKey = "CurrentActivityWindowIsOpen";
    private static CurrentActivityWindow? _current;
    private readonly CurrentActivityViewModel _model;
    private bool _closed;
    public static bool ShouldOpenAtStartup => AppDefaults.Shared.GetValue(OpenKey, false);

    public static CurrentActivityWindow Show()
    {
        _current ??= new();
        _current.Activate();
        _current._model.Start();
        return _current;
    }

    public sealed class Row(ActivitySnapshot activity)
    {
        private readonly ActivityDisplayText _text = CurrentActivityViewModel.DisplayText(activity);
        public int ID => activity.ID;
        public string Owner => activity.Owner.DisplayName;
        public string StateLabel => CurrentActivityViewModel.AccessibilityLabel(activity.State);
        public string Title => _text.Title;
        public string Detail => _text.Detail is { } detail ? " " + detail : "";
        public string Description => Title + Detail;
        public Visibility Pending => Visible(ActivityState.Pending);
        public Visibility Running => Visible(ActivityState.Running);
        public Visibility Completed => Visible(ActivityState.Completed);
        public Visibility Failed => Visible(ActivityState.Failed);
        private Visibility Visible(ActivityState state) => activity.State == state ? Visibility.Visible : Visibility.Collapsed;
    }

    private CurrentActivityWindow()
    {
        InitializeComponent();
        Title = Heading.Text = AppStrings.Get("CurrentActivity");
        EmptyState.Text = AppStrings.Get("NoCurrentActivity");
        HelpButton.Content = AppStrings.Get("Help");
        CloseButton.Content = AppStrings.Get("Close");
        AutomationProperties.SetName(Activities, Heading.Text);
        WindowIconHelper.ApplyFlatIcon(this);
        WindowThemeHelper.Attach(this);
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32(700, 360));
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new(area.X + Math.Max(0, (area.Width - 700) / 2),
            area.Y + Math.Max(0, (area.Height - 360) / 2 - 40)));
        _model = new CurrentActivityViewModel(WinNewsWire.ActivityLog.ActivityLog.Shared, action =>
        {
            if (!DispatcherQueue.TryEnqueue(() => { if (!_closed) action(); }))
                throw new InvalidOperationException("The Current Activity window dispatcher is unavailable.");
        });
        _model.DisplayedActivitiesDidChange += RowsChanged;
        void OnClosed(object sender, WindowEventArgs args)
        {
            Closed -= OnClosed;
            _closed = true;
            _model.DisplayedActivitiesDidChange -= RowsChanged;
            _model.Dispose();
            if (!App.IsShuttingDown) SaveOpenState(false);
            _current = null;
        }
        Closed += OnClosed;
        SaveOpenState(true);
    }

    private void RowsChanged(object? sender, EventArgs args)
    {
        if (_closed) return;
        var rows = _model.DisplayedActivities.Select(activity => new Row(activity)).ToArray();
        Activities.ItemsSource = rows;
        EmptyState.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveOpenState(bool open)
    {
        try { AppDefaults.Shared.SetValue(OpenKey, open); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            App.LogException("Save Current Activity state", error);
            if (!_closed) StatusText.Text = error.Message;
        }
    }

    private async void Help_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            if (!await global::Windows.System.Launcher.LaunchUriAsync(new Uri("https://netnewswire.com/help/current-activity.html")))
                throw new InvalidOperationException("Windows could not open the Current Activity help page.");
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            App.LogException("Current Activity help", error);
            if (!_closed) StatusText.Text = error.Message;
        }
    }
    private void Close_Click(object sender, RoutedEventArgs args) => Close();
}
