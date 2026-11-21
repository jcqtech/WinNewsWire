using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using WinRT.Interop;

namespace WinNewsWire;

public sealed partial class MainContent
{
    private const double FloatingToolbarSlideDistance = 8;
    private DispatcherQueueTimer? _floatingToolbarPointerTimer;
    private Window? _floatingToolbarWindow;
    private IntPtr _floatingToolbarWindowHandle;
    private bool _floatingToolbarWindowActive;
    private bool _floatingToolbarPointerFailed;
    private Point _floatingToolbarContentOrigin;
    private FloatingToolbarCursorInfo? _floatingToolbarLastCursor;
    private bool _floatingToolbarRevealed;
    private bool _floatingToolbarTouchMode;
    private bool _floatingToolbarFocusVisible;
    private bool _floatingToolbarMenuOpen;
    private Point? _floatingToolbarPointerPosition;
    private Storyboard? _floatingToolbarMotion;

    private void InitializeFloatingToolbar()
    {
        // WebView2 owns a child HWND, so its pointer motion does not bubble through XAML.
        _floatingToolbarPointerTimer = DispatcherQueue.CreateTimer();
        _floatingToolbarPointerTimer.Interval = TimeSpan.FromMilliseconds(50);
        _floatingToolbarPointerTimer.IsRepeating = true;
        _floatingToolbarPointerTimer.Tick += FloatingToolbarPointerTimer_Tick;
        FloatingToolbarButton.GotFocus += FloatingToolbar_FocusChanged;
        FloatingToolbarButton.LostFocus += FloatingToolbar_FocusChanged;
        FloatingToolbarMenu.Opening += FloatingToolbarMenu_Opening;
        FloatingToolbarMenu.Closed += FloatingToolbarMenu_Closed;
    }

    private void UpdateFloatingToolbarPlacement()
    {
        if (_shuttingDown || !IsLoaded) return;
        if (_floatingToolbarWindow is null)
        {
            _floatingToolbarWindow = App.WindowForXamlRoot(XamlRoot)
                ?? throw new InvalidOperationException("The floating toolbar's window is unavailable.");
            _floatingToolbarWindowHandle = WindowNative.GetWindowHandle(_floatingToolbarWindow);
            _floatingToolbarWindowActive = FloatingToolbarForegroundWindow() == _floatingToolbarWindowHandle;
            _floatingToolbarWindow.Activated += FloatingToolbarWindow_Activated;
        }
        _floatingToolbarContentOrigin = MainContentGrid.TransformToVisual((UIElement)_floatingToolbarWindow.Content)
            .TransformPoint(default);
        ReadFloatingToolbarPointer(releaseFocus: true);
        UpdateFloatingToolbarReveal();
    }

    private void FloatingToolbarWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_shuttingDown) return;
        _floatingToolbarWindowActive = args.WindowActivationState != WindowActivationState.Deactivated;
        _floatingToolbarPointerFailed = false;
        if (_floatingToolbarWindowActive) ReadFloatingToolbarPointer(releaseFocus: false);
        UpdateFloatingToolbarReveal(animate: false);
    }

    private void FloatingToolbarPointerTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        ReadFloatingToolbarPointer(releaseFocus: true);
        UpdateFloatingToolbarReveal();
    }

    private void ReadFloatingToolbarPointer(bool releaseFocus)
    {
        if (_shuttingDown || _toolbarConfiguration.IsVisible || !_floatingToolbarWindowActive || _floatingToolbarPointerFailed) return;
        var cursor = new FloatingToolbarCursorInfo { Size = (uint)Marshal.SizeOf<FloatingToolbarCursorInfo>() };
        if (!GetFloatingToolbarCursorInfo(ref cursor))
        {
            ReportFloatingToolbarPointerError();
            return;
        }
        bool moved = _floatingToolbarLastCursor is { } previous
            && (previous.Position.X != cursor.Position.X || previous.Position.Y != cursor.Position.Y);
        _floatingToolbarLastCursor = cursor;
        if (moved && releaseFocus) _floatingToolbarFocusVisible = false;
        _floatingToolbarTouchMode = (cursor.Flags & 0x2) != 0; // CURSOR_SUPPRESSED: Windows is using touch or pen.
        var client = cursor.Position;
        if (!FloatingToolbarScreenToClient(_floatingToolbarWindowHandle, ref client))
        {
            ReportFloatingToolbarPointerError();
            return;
        }
        double scale = XamlRoot.RasterizationScale;
        var point = new Point(client.X / scale - _floatingToolbarContentOrigin.X,
            client.Y / scale - _floatingToolbarContentOrigin.Y);
        _floatingToolbarPointerPosition = point.X >= 0 && point.Y >= 0
            && point.X < MainContentGrid.ActualWidth && point.Y < MainContentGrid.ActualHeight ? point : null;
    }

    private void ReportFloatingToolbarPointerError()
    {
        var error = new Win32Exception(Marshal.GetLastWin32Error());
        _floatingToolbarPointerFailed = true;
        _floatingToolbarPointerTimer?.Stop();
        App.LogException("Track floating toolbar pointer", error);
        ViewModel.StatusMessage = "Could not track the toolbar pointer: " + error.Message;
    }

    private void FloatingToolbar_FocusChanged(object sender, RoutedEventArgs args)
    {
        ReadFloatingToolbarPointer(releaseFocus: false);
        _floatingToolbarFocusVisible = FloatingToolbarButton.FocusState is FocusState.Keyboard or FocusState.Programmatic;
        UpdateFloatingToolbarReveal();
    }

    private void FloatingToolbarMenu_Opening(object? sender, object args)
    {
        _floatingToolbarMenuOpen = true;
        UpdateFloatingToolbarReveal(animate: false);
    }

    private void FloatingToolbarMenu_Closed(object? sender, object args)
    {
        _floatingToolbarMenuOpen = false;
        ReadFloatingToolbarPointer(releaseFocus: true);
        UpdateFloatingToolbarReveal();
    }

    private void UpdateFloatingToolbarReveal(bool animate = true)
    {
        if (_shuttingDown || !IsLoaded) return;
        bool active = !_toolbarConfiguration.IsVisible && _floatingToolbarWindowActive;
        if (active && !_floatingToolbarPointerFailed)
        {
            if (_floatingToolbarPointerTimer is { IsRunning: false } timer) timer.Start();
        }
        else
        {
            _floatingToolbarPointerTimer?.Stop();
            _floatingToolbarLastCursor = null;
            _floatingToolbarPointerPosition = null;
        }
        const double vicinity = 48;
        var margin = FloatingToolbarSurface.Margin;
        // Use the resting layout, not the animated position, so hovering cannot chase the moving button.
        var area = new Rect(MainContentGrid.ActualWidth - margin.Right - FloatingToolbarButton.Width - vicinity,
            margin.Top - vicinity, FloatingToolbarButton.Width + vicinity * 2, FloatingToolbarButton.Height + vicinity * 2);
        bool show = active && (_floatingToolbarPointerFailed || _floatingToolbarTouchMode || _floatingToolbarFocusVisible
            || _floatingToolbarMenuOpen || _floatingToolbarPointerPosition is { } point && area.Contains(point));
        animate &= active;
        if (_floatingToolbarRevealed == show && (animate || _floatingToolbarMotion is null)) return;
        _floatingToolbarRevealed = show;
        double opacity = FloatingToolbarSurface.Opacity, offset = FloatingToolbarTranslation.X;
        StopFloatingToolbarMotion();
        FloatingToolbarSurface.Opacity = show ? 1 : 0;
        FloatingToolbarTranslation.X = show ? 0 : FloatingToolbarSlideDistance;
        if (!animate || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;

        var duration = new Duration(TimeSpan.FromMilliseconds(160));
        var fade = new DoubleAnimation { From = opacity, To = FloatingToolbarSurface.Opacity, Duration = duration };
        var slide = new DoubleAnimation { From = offset, To = FloatingToolbarTranslation.X, Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(fade, FloatingToolbarSurface);
        Storyboard.SetTargetProperty(fade, nameof(Opacity));
        Storyboard.SetTarget(slide, FloatingToolbarTranslation);
        Storyboard.SetTargetProperty(slide, nameof(TranslateTransform.X));
        _floatingToolbarMotion = new Storyboard();
        _floatingToolbarMotion.Children.Add(fade);
        _floatingToolbarMotion.Children.Add(slide);
        _floatingToolbarMotion.Completed += FloatingToolbarMotion_Completed;
        _floatingToolbarMotion.Begin();
    }

    private void FloatingToolbarMotion_Completed(object? sender, object args)
    {
        if (ReferenceEquals(sender, _floatingToolbarMotion)) StopFloatingToolbarMotion();
    }

    private void StopFloatingToolbarMotion()
    {
        if (_floatingToolbarMotion is not { } motion) return;
        motion.Completed -= FloatingToolbarMotion_Completed;
        motion.Stop();
        _floatingToolbarMotion = null;
    }

    private void ShutdownFloatingToolbar()
    {
        if (_floatingToolbarPointerTimer is { } timer)
        {
            timer.Stop();
            timer.Tick -= FloatingToolbarPointerTimer_Tick;
            _floatingToolbarPointerTimer = null;
        }
        if (_floatingToolbarWindow is { } window) window.Activated -= FloatingToolbarWindow_Activated;
        _floatingToolbarWindow = null;
        StopFloatingToolbarMotion();
        FloatingToolbarButton.GotFocus -= FloatingToolbar_FocusChanged;
        FloatingToolbarButton.LostFocus -= FloatingToolbar_FocusChanged;
        FloatingToolbarMenu.Opening -= FloatingToolbarMenu_Opening;
        FloatingToolbarMenu.Closed -= FloatingToolbarMenu_Closed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FloatingToolbarPoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FloatingToolbarCursorInfo
    {
        public uint Size, Flags;
        public IntPtr Cursor;
        public FloatingToolbarPoint Position;
    }

    [DllImport("user32.dll", EntryPoint = "GetCursorInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFloatingToolbarCursorInfo(ref FloatingToolbarCursorInfo info);

    [DllImport("user32.dll", EntryPoint = "ScreenToClient", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FloatingToolbarScreenToClient(IntPtr window, ref FloatingToolbarPoint point);

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr FloatingToolbarForegroundWindow();
}
