using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace WinNewsWire.Input;

/// <summary>
/// Enables standard Windows middle-mouse-button scroll on a <see cref="ListView"/>
/// (or any control backed by a <see cref="ScrollViewer"/>).
///
/// Interaction model:
///   • Middle-button press+hold: enters drag-scroll. While the button is held, vertical
///     movement of the cursor is translated into continuous scrolling. Distance from the
///     press origin controls scroll speed — the farther the cursor, the faster the scroll.
///   • Middle-button release: ends drag-scroll.
///
/// This mirrors the middle-click autoscroll users expect from browsers / File Explorer.
/// WinUI 3's ScrollViewer has no built-in middle-click autoscroll, so we implement it
/// in user code.
/// </summary>
internal sealed class MiddleClickAutoScroll
{
    private readonly FrameworkElement _owner;
    private ScrollViewer? _scrollViewer;
    private DispatcherTimer? _timer;
    private Point _origin;
    private Point _current;
    private bool _active;
    private uint _capturedPointerId;

    public static void Attach(FrameworkElement owner)
    {
        _ = new MiddleClickAutoScroll(owner);
    }

    private MiddleClickAutoScroll(FrameworkElement owner)
    {
        _owner = owner;
        owner.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(OnPointerPressed), handledEventsToo: true);
        owner.AddHandler(UIElement.PointerMovedEvent,
            new PointerEventHandler(OnPointerMoved), handledEventsToo: true);
        owner.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler(OnPointerReleased), handledEventsToo: true);
        owner.AddHandler(UIElement.PointerCaptureLostEvent,
            new PointerEventHandler(OnPointerCaptureLost), handledEventsToo: true);
    }

    private ScrollViewer? FindScrollViewer()
    {
        if (_scrollViewer is not null) return _scrollViewer;
        _scrollViewer = FindDescendant<ScrollViewer>(_owner);
        return _scrollViewer;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) return t;
            var nested = FindDescendant<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse) return;
        var point = e.GetCurrentPoint(_owner);
        if (!point.Properties.IsMiddleButtonPressed) return;
        var sv = FindScrollViewer();
        if (sv is null) return;

        _origin = point.Position;
        _current = point.Position;
        _active = true;
        _capturedPointerId = e.Pointer.PointerId;
        _owner.CapturePointer(e.Pointer);

        _timer ??= CreateTimer();
        _timer.Start();

        // Swallow the middle-click so ListView doesn't try to select/toggle on it.
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_active) return;
        _current = e.GetCurrentPoint(_owner).Position;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_active) return;
        var point = e.GetCurrentPoint(_owner);
        // Middle-button release ends the drag. (Other buttons releasing during drag are ignored.)
        if (!point.Properties.IsMiddleButtonPressed)
        {
            Stop(e.Pointer);
            e.Handled = true;
        }
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_active) Stop(e.Pointer);
    }

    private void Stop(Pointer? pointer)
    {
        _active = false;
        _timer?.Stop();
        if (pointer is not null)
        {
            try { _owner.ReleasePointerCapture(pointer); } catch { }
        }
    }

    private DispatcherTimer CreateTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        t.Tick += (_, _) =>
        {
            if (!_active) { t.Stop(); return; }
            var sv = FindScrollViewer();
            if (sv is null) { t.Stop(); return; }

            double dy = _current.Y - _origin.Y;
            double dx = _current.X - _origin.X;
            // Dead-zone matching Windows' autoscroll cursor — no scroll within ~12px of origin.
            const double deadZone = 12.0;
            double vy = Math.Abs(dy) < deadZone ? 0 : Math.Sign(dy) * ComputeSpeed(Math.Abs(dy) - deadZone);
            double vx = Math.Abs(dx) < deadZone ? 0 : Math.Sign(dx) * ComputeSpeed(Math.Abs(dx) - deadZone);
            if (vy == 0 && vx == 0) return;

            var newY = Math.Clamp(sv.VerticalOffset + vy, 0, sv.ScrollableHeight);
            var newX = Math.Clamp(sv.HorizontalOffset + vx, 0, sv.ScrollableWidth);
            sv.ChangeView(newX, newY, zoomFactor: null, disableAnimation: true);
        };
        return t;
    }

    /// <summary>Quadratic ramp — smooth start, accelerates as the cursor moves away.</summary>
    private static double ComputeSpeed(double distance)
    {
        double d = Math.Min(distance, 400);
        return (d * d) / 1600.0; // 0 → 100 px/tick across the sane range; timer runs ~60Hz
    }
}
