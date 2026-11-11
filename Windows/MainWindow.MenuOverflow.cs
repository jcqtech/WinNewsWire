using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinNewsWire.AppShared;

namespace WinNewsWire;

public sealed partial class MainWindow
{
    private MenuBarItem[] _menuHeaders = [];
    private readonly HashSet<Popup> _pendingMenuPopups = [];
    private bool _menuLayoutQueued;
    private bool _measuringMenus;
    private bool _menuContentsDirty = true;
    private int _visibleMenuCount;

    private void InitializeMenuOverflow()
    {
        _menuHeaders = AppMenuBar.Items.Where(item => item != MenuOverflowItem).ToArray();
        _visibleMenuCount = _menuHeaders.Length;
        AutomationProperties.SetName(MenuOverflowItem, AppStrings.Get("MoreMenus"));
        ToolTipService.SetToolTip(MenuOverflowItem, AppStrings.Get("MoreMenus"));
    }

    private void MenuBar_Loaded(object sender, RoutedEventArgs args) => QueueMenuOverflowLayout();
    private void MenuLayout_SizeChanged(object sender, SizeChangedEventArgs args) => QueueMenuOverflowLayout();

    private void RefreshMenuOverflowContents()
    {
        _menuContentsDirty = true;
        QueueMenuOverflowLayout();
    }

    private void QueueMenuOverflowLayout()
    {
        if (_closed || _measuringMenus || _menuLayoutQueued || _menuHeaders.Length == 0) return;
        _menuLayoutQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _menuLayoutQueued = false;
            try { UpdateMenuOverflow(); }
            catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
            {
                App.LogException("Update menu overflow", error);
                throw;
            }
        }))
        {
            _menuLayoutQueued = false;
            App.LogException("Update menu overflow", null, "The window dispatcher is unavailable.");
        }
    }

    private void UpdateMenuOverflow()
    {
        if (_closed || !AppMenuBar.IsLoaded || MenuBarHost.ActualWidth <= 0) return;
        var popups = VisualTreeHelper.GetOpenPopupsForXamlRoot(AppMenuBar.XamlRoot).ToArray();
        if (popups.Length > 0)
        {
            foreach (var popup in popups)
                if (_pendingMenuPopups.Add(popup)) popup.Closed += MenuPopup_Closed;
            return;
        }

        var focused = FocusManager.GetFocusedElement(AppMenuBar.XamlRoot) as DependencyObject;
        while (focused is not null && focused is not MenuBarItem) focused = VisualTreeHelper.GetParent(focused);
        _measuringMenus = true;
        try
        {
            foreach (var header in _menuHeaders) header.Visibility = Visibility.Visible;
            MenuOverflowItem.Visibility = Visibility.Visible;
            AppMenuBar.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            double scale = AppMenuBar.XamlRoot.RasterizationScale;
            double Width(FrameworkElement element) => Math.Ceiling(element.DesiredSize.Width * scale) / scale;
            double headersWidth = _menuHeaders.Sum(Width);
            double overflowWidth = Width(MenuOverflowItem);
            double chrome = Math.Max(0, AppMenuBar.DesiredSize.Width - _menuHeaders.Sum(header => header.DesiredSize.Width)
                - MenuOverflowItem.DesiredSize.Width);
            double available = Math.Floor(MenuBarHost.ActualWidth * scale) / scale;
            int visible = _menuHeaders.Length;
            if (chrome + headersWidth > available)
            {
                visible = 0;
                double used = chrome + overflowWidth;
                while (visible < _menuHeaders.Length && used + Width(_menuHeaders[visible]) <= available)
                    used += Width(_menuHeaders[visible++]);
            }

            if (visible != _visibleMenuCount || _menuContentsDirty)
            {
                // MenuBarItem mirrors insert/remove events into its native flyout, not collection resets.
                while (MenuOverflowItem.Items.Count > 0) MenuOverflowItem.Items.RemoveAt(0);
                foreach (var header in _menuHeaders.Skip(visible))
                {
                    var submenu = new MenuFlyoutSubItem { AccessKey = header.AccessKey, Tag = header };
                    submenu.SetBinding(MenuFlyoutSubItem.TextProperty, MenuBinding(header, nameof(header.Title)));
                    foreach (var command in header.Items) submenu.Items.Add(CreateMenuProxy(command));
                    MenuOverflowItem.Items.Add(submenu);
                }
                _visibleMenuCount = visible;
                _menuContentsDirty = false;
            }
            foreach (var header in _menuHeaders.Skip(visible)) header.Visibility = Visibility.Collapsed;
            MenuOverflowItem.Visibility = visible < _menuHeaders.Length ? Visibility.Visible : Visibility.Collapsed;
            if (focused is MenuBarItem { Visibility: Visibility.Collapsed })
                (visible < _menuHeaders.Length ? MenuOverflowItem : _menuHeaders[^1]).Focus(FocusState.Programmatic);
        }
        finally { _measuringMenus = false; }
    }

    private MenuFlyoutItemBase CreateMenuProxy(MenuFlyoutItemBase original)
    {
        MenuFlyoutItemBase proxy;
        if (original is MenuFlyoutSubItem submenu)
        {
            var copy = new MenuFlyoutSubItem();
            copy.SetBinding(MenuFlyoutSubItem.TextProperty, MenuBinding(submenu, nameof(submenu.Text)));
            foreach (var child in submenu.Items) copy.Items.Add(CreateMenuProxy(child));
            proxy = copy;
        }
        else if (original is MenuFlyoutItem command)
        {
            MenuFlyoutItem copy;
            if (command is ToggleMenuFlyoutItem toggle)
            {
                var check = new ToggleMenuFlyoutItem();
                check.SetBinding(ToggleMenuFlyoutItem.IsCheckedProperty, MenuBinding(toggle, nameof(toggle.IsChecked)));
                copy = check;
            }
            else copy = new MenuFlyoutItem();
            copy.SetBinding(MenuFlyoutItem.TextProperty, MenuBinding(command, nameof(command.Text)));
            copy.KeyboardAcceleratorTextOverride = command.KeyboardAcceleratorTextOverride;
            copy.Click += MenuProxy_Click;
            proxy = copy;
        }
        else if (original is MenuFlyoutSeparator) proxy = new MenuFlyoutSeparator();
        else throw new NotSupportedException("Unsupported overflow menu item: " + original.GetType().Name);
        proxy.Name = original.Name;
        proxy.Tag = original;
        proxy.AccessKey = original.AccessKey;
        proxy.SetBinding(Control.IsEnabledProperty, MenuBinding(original, nameof(original.IsEnabled)));
        proxy.SetBinding(UIElement.VisibilityProperty, MenuBinding(original, nameof(original.Visibility)));
        return proxy;
    }

    private static Binding MenuBinding(object source, string property)
        => new() { Source = source, Path = new PropertyPath(property), Mode = BindingMode.OneWay };

    private void MenuProxy_Click(object sender, RoutedEventArgs args)
    {
        var original = (MenuFlyoutItem)((MenuFlyoutItem)sender).Tag;
        // Invoke the canonical item so its existing handlers, parameters and toggle behavior remain authoritative.
        var peer = FrameworkElementAutomationPeer.FromElement(original) ?? FrameworkElementAutomationPeer.CreatePeerForElement(original);
        if (original is ToggleMenuFlyoutItem && peer?.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle)
            toggle.Toggle();
        else if (peer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider command)
            command.Invoke();
        else throw new InvalidOperationException("The original menu command cannot be invoked: " + original.Text);
    }

    private void MenuPopup_Closed(object? sender, object args)
    {
        if (sender is Popup popup)
        {
            popup.Closed -= MenuPopup_Closed;
            _pendingMenuPopups.Remove(popup);
        }
        QueueMenuOverflowLayout();
    }

    private void StopMenuOverflowLayout()
    {
        foreach (var popup in _pendingMenuPopups) popup.Closed -= MenuPopup_Closed;
        _pendingMenuPopups.Clear();
    }
}
