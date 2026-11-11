using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinNewsWire.Models;
using WinRT.Interop;

namespace WinNewsWire;

public sealed partial class MainWindow
{
    private WindowBounds _normalBounds = new(0, 0, 1400, 900);
    private bool _wasMaximized;
    private MainWindowState? _pendingState;
    private bool _pendingFullScreenRestore;
    private bool _changingPresentation;
    private readonly TaskCompletionSource _presentationRestored = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task<bool> Restoration { get; private set; } = Task.FromResult(true);
    internal bool IsFullScreen => !_closed && AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;

    private int MinimumWindowWidth
    {
        get
        {
            uint dpi = GetDpiForWindow(WindowNative.GetWindowHandle(this));
            if (dpi == 0) throw new InvalidOperationException("The reader window's display scale is unavailable.");
            int frame = AppWindow.Size.Width - AppWindow.ClientSize.Width;
            return MainContentControl.MinimumReaderWidthInPixels(dpi / 96d) + frame;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    private void UpdateMinimumWindowWidth()
    {
        if (_closed || AppWindow.Presenter is not OverlappedPresenter presenter) return;
        int minimum = MinimumWindowWidth;
        if (presenter.PreferredMinimumWidth != minimum) presenter.PreferredMinimumWidth = minimum;
    }

    private void InitializeRestoration(MainWindowState? state)
    {
        UpdateMinimumWindowWidth();
        UpdateWindowPlacement();
        _pendingFullScreenRestore = state?.IsFullScreen == true;
        if (_pendingFullScreenRestore) Activated += RestoreFullScreenWhenActivated;
        else _presentationRestored.TrySetResult();
        if (state is not null)
        {
            _pendingState = state;
            var bounds = ClampToDisplay(state.Bounds);
            // Move to the saved display before applying its DPI-dependent minimum.
            AppWindow.MoveAndResize(bounds);
            bounds = ClampToDisplay(state.Bounds, MinimumWindowWidth);
            UpdateMinimumWindowWidth();
            AppWindow.MoveAndResize(bounds);
            _normalBounds = new(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            _wasMaximized = state.IsMaximized;
            if (state.IsMaximized && AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
            MainContentControl.IsEnabled = false;
            Restoration = RestoreStateAsync(state);
        }
        AppWindow.Changed += WindowPlacementChanged;
        Closed += WindowRestoration_Closed;
    }

    private void WindowRestoration_Closed(object sender, WindowEventArgs args)
    {
        Closed -= WindowRestoration_Closed;
        AppWindow.Changed -= WindowPlacementChanged;
        Activated -= RestoreFullScreenWhenActivated;
        _presentationRestored.TrySetCanceled();
    }

    internal MainWindowState CaptureState()
    {
        UpdateWindowPlacement();
        var state = _pendingState is { } pending
            ? pending with { Bounds = _normalBounds, IsMaximized = _wasMaximized }
            : MainContentControl.CaptureState(_normalBounds, _wasMaximized);
        return state with { IsFullScreen = _pendingState?.IsFullScreen ?? (IsFullScreen || _pendingFullScreenRestore) };
    }

    internal Task RefreshScrollPositionAsync()
        => _closed || _pendingState is not null ? Task.CompletedTask
            : MainContentControl.CaptureScrollPositionAsync().WaitAsync(TimeSpan.FromSeconds(10));

    internal async Task<MainWindowState> CaptureStateAsync()
    {
        await RefreshScrollPositionAsync();
        return CaptureState();
    }

    private void WindowPlacementChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        UpdateWindowPlacement();
        if (!_closed)
        {
            UpdateMinimumWindowWidth();
            UpdateTitleBarLayout();
        }
        if (args.DidPresenterChange && !_closed)
        {
            SyncMenuToggles();
        }
    }

    private void UpdateWindowPlacement()
    {
        if (_closed || _changingPresentation || AppWindow.Presenter is not OverlappedPresenter presenter) return;
        if (presenter.State == OverlappedPresenterState.Minimized) return;
        _wasMaximized = presenter.State == OverlappedPresenterState.Maximized;
        if (!_wasMaximized && AppWindow.Size.Width > 0 && AppWindow.Size.Height > 0)
            _normalBounds = new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
    }

    private void RestoreFullScreenWhenActivated(object sender, WindowActivatedEventArgs args)
    {
        if (!_pendingFullScreenRestore || args.WindowActivationState == WindowActivationState.Deactivated) return;
        _pendingFullScreenRestore = false;
        try
        {
            SetFullScreen(true);
            _presentationRestored.TrySetResult();
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            _presentationRestored.TrySetException(error);
        }
    }

    private void SetFullScreen(bool fullScreen)
    {
        if (_closed || IsFullScreen == fullScreen) return;
        UpdateWindowPlacement();
        var bounds = _normalBounds;
        bool maximized = _wasMaximized;
        _changingPresentation = true;
        try
        {
            AppWindow.SetPresenter(fullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
            if (!fullScreen)
            {
                AppWindow.MoveAndResize(new(bounds.X, bounds.Y, bounds.Width, bounds.Height));
                if (maximized && AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
            }
        }
        finally { _changingPresentation = false; }
        _normalBounds = bounds;
        _wasMaximized = maximized;
        SyncMenuToggles();
    }

    internal static RectInt32 ClampToDisplay(WindowBounds bounds, int minimumWidth = MainContent.ArticlePaneMinimumWidth)
    {
        var rectangle = new RectInt32(bounds.X, bounds.Y, Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
        var workArea = DisplayArea.GetFromRect(rectangle, DisplayAreaFallback.Nearest).WorkArea;
        int width = Math.Clamp(bounds.Width, Math.Min(minimumWidth, workArea.Width), workArea.Width);
        int height = Math.Clamp(bounds.Height, Math.Min(480, workArea.Height), workArea.Height);
        int x = (int)Math.Clamp((long)bounds.X, workArea.X, (long)workArea.X + workArea.Width - width);
        int y = (int)Math.Clamp((long)bounds.Y, workArea.Y, (long)workArea.Y + workArea.Height - height);
        return new(x, y, width, height);
    }

    private async Task<bool> RestoreStateAsync(MainWindowState state)
    {
        try
        {
            await _presentationRestored.Task;
            await MainContentControl.RestoreStateAsync(state);
            if (_closed || MainContentControl.IsShuttingDown) return false;
            _pendingState = null;
            SyncMenuToggles();
            return true;
        }
        catch (OperationCanceledException) when (_closed || MainContentControl.IsShuttingDown) { return false; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or COMException)
        {
            ReportStateError("Restore window", error);
            return false;
        }
        finally
        {
            if (!_closed && !MainContentControl.IsShuttingDown) MainContentControl.IsEnabled = true;
        }
    }

    internal void ReportStateError(string operation, Exception error)
    {
        App.LogException(operation, error);
        if (!_closed && !MainContentControl.IsShuttingDown)
            MainContentControl.ViewModel.StatusMessage = $"{operation} failed: {error.Message}";
    }
}
