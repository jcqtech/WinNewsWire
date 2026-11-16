using System;
using System.Collections.Generic;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinNewsWire.Core;

namespace WinNewsWire.Input;

internal sealed class WheelScrollingBehavior : IDisposable
{
    private readonly FrameworkElement _owner;
    private readonly ScrollViewer _scroll;
    private readonly UIElement _surface;
    private readonly List<UIElement> _targets = [];
    private readonly PointerEventHandler _pointerHandler;
    private readonly PointerEventHandler _wheelHandler;
    private int _horizontalDirection, _verticalDirection;

    internal WheelScrollingBehavior(FrameworkElement owner, ScrollViewer scroll)
    {
        _owner = owner;
        _scroll = scroll;
        scroll.ApplyTemplate();
        _surface = Find(scroll, "ScrollContentPresenter") as UIElement ?? scroll.Content as UIElement
            ?? throw new InvalidOperationException("The native scroll surface was not created.");
        _scroll.IsScrollInertiaEnabled = AppDefaults.Shared.UseWheelInertia;
        _pointerHandler = OnPointerInput;
        _wheelHandler = OnWheel;
        owner.AddHandler(UIElement.PointerEnteredEvent, _pointerHandler, handledEventsToo: true);
        owner.AddHandler(UIElement.PointerPressedEvent, _pointerHandler, handledEventsToo: true);
        foreach (var target in new[] { _surface, Find(scroll, "VerticalScrollBar") as UIElement,
            Find(scroll, "HorizontalScrollBar") as UIElement })
        {
            if (target is null || _targets.Contains(target)) continue;
            _targets.Add(target);
            target.AddHandler(UIElement.PointerWheelChangedEvent, _wheelHandler, handledEventsToo: true);
        }
    }

    private void OnPointerInput(object sender, PointerRoutedEventArgs args)
    {
        bool touch = args.Pointer.PointerDeviceType is PointerDeviceType.Touch or PointerDeviceType.Pen;
        SetInertia(touch || AppDefaults.Shared.UseWheelInertia);
        if (touch) _horizontalDirection = _verticalDirection = 0;
    }

    private void OnWheel(object sender, PointerRoutedEventArgs args)
    {
        var properties = args.GetCurrentPoint(_owner).Properties;
        if (properties.MouseWheelDelta == 0 || args.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control)) return;
        bool horizontal = properties.IsHorizontalMouseWheel;
        if ((horizontal ? _scroll.HorizontalScrollMode : _scroll.VerticalScrollMode) == ScrollMode.Disabled) return;
        bool inertia = AppDefaults.Shared.UseWheelInertia;
        SetInertia(inertia);
        if (!inertia)
        {
            _horizontalDirection = _verticalDirection = 0;
            return;
        }
        int direction = Math.Sign(properties.MouseWheelDelta);
        if (direction != (horizontal ? _horizontalDirection : _verticalDirection)) StopAnimation();
        if (horizontal) _horizontalDirection = direction;
        else _verticalDirection = direction;
    }

    private void SetInertia(bool enabled)
    {
        if (!enabled && _scroll.IsScrollInertiaEnabled) StopAnimation();
        _scroll.IsScrollInertiaEnabled = enabled;
    }

    private void StopAnimation()
    {
        double x = _scroll.HorizontalOffset, y = _scroll.VerticalOffset;
        _surface.CancelDirectManipulations();
        _scroll.ChangeView(x, y, null, disableAnimation: true);
    }

    private static FrameworkElement? Find(DependencyObject root, string name)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is ScrollViewer) continue;
            if (child is FrameworkElement element && element.Name == name) return element;
            if (Find(child, name) is { } match) return match;
        }
        return null;
    }

    public void Dispose()
    {
        _owner.RemoveHandler(UIElement.PointerEnteredEvent, _pointerHandler);
        _owner.RemoveHandler(UIElement.PointerPressedEvent, _pointerHandler);
        foreach (var target in _targets) target.RemoveHandler(UIElement.PointerWheelChangedEvent, _wheelHandler);
        _targets.Clear();
    }
}
