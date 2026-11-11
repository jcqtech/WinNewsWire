#if PARITY_PROBE
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinNewsWire.Core;
using WinRT.Interop;

namespace WinNewsWire.Diagnostics;

internal static partial class ParityProbe
{
    private static IExpandCollapseProvider MenuExpansion(FrameworkElement element)
    {
        var peer = FrameworkElementAutomationPeer.FromElement(element) ?? FrameworkElementAutomationPeer.CreatePeerForElement(element);
        return peer?.GetPattern(PatternInterface.ExpandCollapse) as IExpandCollapseProvider
            ?? throw new InvalidOperationException("The menu has no native ExpandCollapse pattern.");
    }

    private static bool HasMenuPopup(MainWindow window)
        => VisualTreeHelper.GetOpenPopupsForXamlRoot(window.ProbeContent.XamlRoot)
            .SelectMany(popup => Descendants(popup.Child)).OfType<MenuFlyoutPresenter>().Any();

    private static async Task CloseOverflowMenuAsync(MainWindow window, MenuBarItem overflow)
    {
        if (!HasMenuPopup(window) && (overflow.Visibility == Visibility.Collapsed || !overflow.IsLoaded)) return;
        var expansion = MenuExpansion(overflow);
        if (expansion.ExpandCollapseState != ExpandCollapseState.Collapsed) expansion.Collapse();
        await WaitFor(() => Task.FromResult(!HasMenuPopup(window) && expansion.ExpandCollapseState == ExpandCollapseState.Collapsed),
            "Closed overflow menus");
    }

    private static async Task<object?> CheckMenuOverflowAsync(MainWindow window)
    {
        var content = window.ProbeContent;
        var original = window.CaptureState();
        bool originalColumn = AppDefaults.Shared.UseColumnLayout;
        var bar = Descendants(window.Content).OfType<MenuBar>().Single(item => item.Name == "AppMenuBar");
        var host = Descendants(window.Content).OfType<Grid>().Single(item => item.Name == "MenuBarHost");
        var overflow = bar.Items.Single(item => item.Name == "MenuOverflowItem");
        var headers = bar.Items.Where(item => item != overflow).ToArray();
        var titles = headers.ToDictionary(item => item, item => item.Title);
        var viewHeader = headers.Single(item => item.Title == "View");
        Dictionary<MenuBarItem, MenuFlyoutItemBase[]>? originalItems = null;
        MainWindow? other = null;
        CapturePoint? originalCursor = null, clickPoint = null;
        var measurements = new List<object>();
        void Phase(string name) => System.IO.File.WriteAllText(System.IO.Path.Combine(OutputDirectory, "menu-overflow-phase.json"),
            System.Text.Json.JsonSerializer.Serialize(new { Phase = name, Width = host.ActualWidth,
                OuterWidth = window.AppWindow.Size.Width, ClientWidth = window.AppWindow.ClientSize.Width,
                MinimumWidth = (window.AppWindow.Presenter as OverlappedPresenter)?.PreferredMinimumWidth,
                Popups = VisualTreeHelper.GetOpenPopupsForXamlRoot(content.XamlRoot).Select(popup => popup.Child?.GetType().Name),
                Menus = bar.Items.Select(item => new { item.Title, Visibility = item.Visibility.ToString(), item.ActualWidth, Items = item.Items.Count }) }));
        global::Windows.Foundation.Rect Bounds(FrameworkElement element) => element.TransformToVisual(host)
            .TransformBounds(new(0, 0, element.ActualWidth, element.ActualHeight));
        async Task Resize(double width)
        {
            int minimum = ((OverlappedPresenter)window.AppWindow.Presenter).PreferredMinimumWidth ?? 0;
            var bounds = MainWindow.ClampToDisplay(original.Bounds with
                { Width = Math.Max(minimum, (int)Math.Ceiling(width * content.XamlRoot.RasterizationScale)) });
            window.AppWindow.MoveAndResize(bounds);
            await WaitFor(() => Task.FromResult(window.AppWindow.Size.Width == bounds.Width
                && Math.Abs(host.ActualWidth * content.XamlRoot.RasterizationScale - window.AppWindow.ClientSize.Width) < 2),
                "Settled menu viewport at " + width);
        }
        bool Fits() => bar.Items.Where(item => item.Visibility == Visibility.Visible)
            .All(item => item.ActualWidth > 0 && Bounds(item).Left >= -1 && Bounds(item).Right <= host.ActualWidth + 1);
        void VerifyItems()
        {
            int visible = headers.Count(item => item.Visibility == Visibility.Visible);
            Require(headers.Take(visible).All(item => item.Visibility == Visibility.Visible)
                && headers.Skip(visible).All(item => item.Visibility == Visibility.Collapsed), "Menu overflow did not preserve the visible prefix.");
            var hidden = overflow.Items.OfType<MenuFlyoutSubItem>().ToArray();
            Require(hidden.Select(item => item.Text).SequenceEqual(headers.Skip(visible).Select(item => item.Title)),
                "The overflow menus are missing, duplicated or out of order.");
            foreach (var header in headers)
            {
                var submenu = hidden.SingleOrDefault(item => item.Text == header.Title);
                Require(header.Items.SequenceEqual(originalItems![header]), "Overflow moved or lost canonical commands in " + header.Title);
                if (submenu is not null)
                    Require(submenu.Items.Select(item => item.Tag).SequenceEqual(header.Items)
                        && submenu.AccessKey == header.AccessKey, "A hidden menu lost its command targets or access key.");
            }
            Require(Fits() && (visible == headers.Length ? overflow.Visibility == Visibility.Collapsed
                : overflow.Visibility == Visibility.Visible && bar.Items.Last(item => item.Visibility == Visibility.Visible) == overflow),
                "The trailing menu is clipped, missing, or shown unnecessarily.");
        }
        try
        {
            await Resize(1280);
            await WaitFor(() => Task.FromResult(overflow.Visibility == Visibility.Collapsed && headers.All(item => item.Items.Count > 0)), "Full menu bar");
            originalItems = headers.ToDictionary(item => item, item => item.Items.ToArray());
            Phase("wide");
            Require(overflow.Title == "\u2026" && AutomationProperties.GetName(overflow) == AppShared.AppStrings.Get("MoreMenus"),
                "The overflow menu lost its label or accessible name.");
            int previousVisible = headers.Length;
            foreach (double width in new[] { 680, 480, 400, 310 })
            {
                await Resize(width);
                await WaitFor(() => Task.FromResult(Fits()), "Fitting menu bar at " + width);
                VerifyItems();
                Phase("width-" + width);
                int visible = headers.Count(item => item.Visibility == Visibility.Visible);
                Require(visible <= previousVisible, "Narrowing the window revealed additional menus.");
                previousVisible = visible;
                measurements.Add(new { Width = host.ActualWidth, Visible = headers.Take(visible).Select(item => item.Title).ToArray(),
                    Hidden = headers.Skip(visible).Select(item => item.Title).ToArray() });
            }
            Require(headers.Single(item => item.Title == "Window").Visibility == Visibility.Collapsed, "The compact Window menu did not overflow.");
            await WaitFor(() => Task.FromResult(Descendants(overflow).OfType<FontIcon>().Any(icon => icon.ActualHeight >= 20)),
                "Rendered overflow icon");
            var glyph = Descendants(overflow).OfType<FontIcon>().Single();
            var glyphBounds = Bounds(glyph);
            var overflowBounds = Bounds(overflow);
            double glyphCenterOffset = Math.Abs(glyphBounds.Top + glyphBounds.Height / 2
                - overflowBounds.Top - overflowBounds.Height / 2);
            Require(glyph.Glyph == "\uE712" && glyph.FontSize == 20 && glyphBounds.Width >= 20 && glyphCenterOffset <= 1,
                "The overflow glyph is too small or not vertically centered.");
            await SaveWindowScreenshotAsync(window, "menu-overflow-compact");
            window.Activate();
            SetForegroundWindow(WindowNative.GetWindowHandle(window));
            await WaitFor(() => Task.FromResult(GetForegroundWindow() == WindowNative.GetWindowHandle(window)), "Owned hidden-menu shortcut");
            Require(overflow.Focus(FocusState.Programmatic), "The overflow header could not receive focus.");
            Phase("before-hidden-shortcut");
            ProbeNativeInput Key(global::Windows.System.VirtualKey key, uint flags = 0)
                => new() { Type = 1, Data = new() { Keyboard = new() { VirtualKey = (ushort)key, Flags = flags } } };
            Require(SendKeyboardInput(4, [Key(global::Windows.System.VirtualKey.Control), Key(global::Windows.System.VirtualKey.M),
                Key(global::Windows.System.VirtualKey.M, 2), Key(global::Windows.System.VirtualKey.Control, 2)], Marshal.SizeOf<ProbeNativeInput>()) == 4,
                "The hidden-menu keyboard shortcut was not delivered.");
            await WaitFor(() => Task.FromResult(((OverlappedPresenter)window.AppWindow.Presenter).State == OverlappedPresenterState.Minimized),
                "Ctrl+M from a hidden menu");
            ((OverlappedPresenter)window.AppWindow.Presenter).Restore();
            window.Activate();
            SetForegroundWindow(WindowNative.GetWindowHandle(window));
            await WaitFor(() => Task.FromResult(((OverlappedPresenter)window.AppWindow.Presenter).State == OverlappedPresenterState.Restored
                && GetForegroundWindow() == WindowNative.GetWindowHandle(window)), "Reader restored after hidden shortcut");
            Phase("shortcut-restored");

            foreach (var pair in titles) pair.Key.Title = pair.Value + " menu with a longer title";
            await Resize(310);
            await WaitFor(() => Task.FromResult(viewHeader.Visibility == Visibility.Collapsed && Fits()), "Long-title menu overflow");
            VerifyItems();
            Phase("long-titles");
            var origin = new CapturePoint();
            var cursor = new CapturePoint();
            Require(ClientToScreen(WindowNative.GetWindowHandle(window), ref origin) && GetCursorPos(out cursor),
                "Could not locate the native menu input.");
            originalCursor = cursor;
            var target = overflow.TransformToVisual((UIElement)window.Content).TransformPoint(new(overflow.ActualWidth / 2, overflow.ActualHeight / 2));
            clickPoint = new() { X = origin.X + (int)Math.Round(target.X * content.XamlRoot.RasterizationScale),
                Y = origin.Y + (int)Math.Round(target.Y * content.XamlRoot.RasterizationScale) };
            Require(GetForegroundWindow() == WindowNative.GetWindowHandle(window)
                && GetAncestor(WindowFromPoint(clickPoint.Value), 2) == WindowNative.GetWindowHandle(window)
                && SetCursorPos(clickPoint.Value.X, clickPoint.Value.Y), "The private overflow header does not own native input.");
            Require(SendInput(2, [new() { Mouse = new() { Flags = 2 } }, new() { Mouse = new() { Flags = 4 } }], Marshal.SizeOf<ScrollInput>()) == 2,
                "The overflow mouse click was not delivered.");
            var view = overflow.Items.OfType<MenuFlyoutSubItem>().Single(item => ReferenceEquals(item.Tag, viewHeader));
            await WaitFor(() => Task.FromResult(view.IsLoaded), "Visible overflow menu entries");
            MenuExpansion(view).Expand();
            var column = view.Items.OfType<ToggleMenuFlyoutItem>().Single(item => item.Name == "MenuColumnLayout");
            await WaitFor(() => Task.FromResult(column.IsLoaded), "Original command inside overflow submenu");
            Phase("before-column-command");
            bool before = AppDefaults.Shared.UseColumnLayout;
            var columnPeer = FrameworkElementAutomationPeer.FromElement(column) ?? FrameworkElementAutomationPeer.CreatePeerForElement(column);
            (columnPeer?.GetPattern(PatternInterface.Toggle) as IToggleProvider
                ?? throw new InvalidOperationException("The overflow toggle command has no Toggle pattern.")).Toggle();
            await WaitFor(() => Task.FromResult(AppDefaults.Shared.UseColumnLayout != before && column.IsChecked == AppDefaults.Shared.UseColumnLayout),
                "Hidden menu command and checked state");
            await CloseOverflowMenuAsync(window, overflow);
            Phase("column-command");

            MenuExpansion(overflow).Expand();
            var editHeader = headers.Single(item => titles[item] == "Edit");
            var edit = overflow.Items.OfType<MenuFlyoutSubItem>().Single(item => ReferenceEquals(item.Tag, editHeader));
            await WaitFor(() => Task.FromResult(edit.IsLoaded), "Edit overflow submenu");
            MenuExpansion(edit).Expand();
            var find = edit.Items.OfType<MenuFlyoutSubItem>().Single(item => item.Text == "Find");
            await WaitFor(() => Task.FromResult(find.IsLoaded), "Nested Find submenu");
            MenuExpansion(find).Expand();
            var findArticle = find.Items.OfType<MenuFlyoutItem>().Single(item => item.Name == "MenuFindArticle");
            await WaitFor(() => Task.FromResult(findArticle.IsLoaded && findArticle.IsEnabled), "Original Find command enabled");
            Require(!string.IsNullOrEmpty(findArticle.KeyboardAcceleratorTextOverride)
                && findArticle.KeyboardAcceleratorTextOverride == ((MenuFlyoutItem)findArticle.Tag).KeyboardAcceleratorTextOverride,
                "The overflow command lost its native shortcut label.");
            InvokeMenuItem(findArticle);
            await WaitFor(() => Task.FromResult(((FrameworkElement)content.FindName("ArticleFindBar")).Visibility == Visibility.Visible),
                "Ordinary command forwarded from overflow");
            InvokeButton((Button)content.FindName("ArticleFindClose"));
            await CloseOverflowMenuAsync(window, overflow);
            Phase("ordinary-command");

            foreach (var pair in titles) pair.Key.Title = pair.Value;
            other = App.OpenMainWindow();
            await WaitFor(() => Task.FromResult(other.ProbeContent.IsLoaded && other.ProbeContent.ProbeWebViewReady), "Companion menu window");
            window.Activate();
            SetForegroundWindow(WindowNative.GetWindowHandle(window));
            await WaitFor(() => Task.FromResult(Fits()), "Compact menus after companion window");
            MenuExpansion(overflow).Expand();
            Phase("dynamic-window-menu");
            var windows = overflow.Items.OfType<MenuFlyoutSubItem>().Single(item => item.Text == "Window");
            await WaitFor(() => Task.FromResult(windows.IsLoaded), "Window overflow submenu");
            MenuExpansion(windows).Expand();
            var minimize = windows.Items.OfType<MenuFlyoutItem>().Single(item => item.Text == "Minimize");
            Require(!string.IsNullOrEmpty(minimize.KeyboardAcceleratorTextOverride)
                && minimize.KeyboardAccelerators.Count == 0, "Shortcut labels were lost or duplicate accelerators were registered.");
            var openWindows = windows.Items.OfType<MenuFlyoutSubItem>().Single(item => item.Name == "OpenWindowsMenu");
            await WaitFor(() => Task.FromResult(openWindows.IsLoaded), "Nested original submenu");
            MenuExpansion(openWindows).Expand();
            await WaitFor(() => Task.FromResult(openWindows.Items.Count == App.MainWindows.Count
                && openWindows.Items.All(item => item.IsLoaded)), "Dynamic main-window entries");
            Require(openWindows.Items.OfType<ToggleMenuFlyoutItem>().Count(item => item.IsChecked) == 1, "The dynamic window menu lost its checked window.");
            await SaveWindowScreenshotAsync(window, "menu-overflow-nested");
            int popupIndex = 0;
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(content.XamlRoot))
                if (popup.Child is MenuFlyoutPresenter presenter)
                {
                    var area = presenter.TransformToVisual((UIElement)window.Content)
                        .TransformBounds(new(0, 0, presenter.ActualWidth, presenter.ActualHeight));
                    await SaveWindowScreenshotAsync(window, "menu-overflow-popup-" + popupIndex++, area);
                }
            Phase("nested-window-entries");

            await Resize(1280);
            await WaitFor(() => Task.FromResult(host.ActualWidth > 1000), "Resizing with menus open");
            if (HasMenuPopup(window))
                Require(overflow.Visibility == Visibility.Visible && windows.IsLoaded, "A still-open menu was replaced during resizing.");
            await CloseOverflowMenuAsync(window, overflow);
            await WaitFor(() => Task.FromResult(overflow.Visibility == Visibility.Collapsed && Fits()), "Menus restored after close");
            VerifyItems();
            Phase("restored-after-popup");
            foreach (double width in new[] { 360, 800, 340, 1000, 400, 1280 })
                window.AppWindow.Resize(new((int)Math.Ceiling(width * content.XamlRoot.RasterizationScale), original.Bounds.Height));
            await WaitFor(() => Task.FromResult(host.ActualWidth > 1000 && overflow.Visibility == Visibility.Collapsed && Fits()), "Rapid menu resize recovery");
            VerifyItems();
            await SaveWindowScreenshotAsync(window, "menu-overflow-restored");
            System.IO.File.WriteAllText(System.IO.Path.Combine(OutputDirectory, "menu-overflow.json"),
                System.Text.Json.JsonSerializer.Serialize(measurements, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return new { ProgressiveCollapse = true, NativeMouseClick = true, CanonicalCommandsPreserved = true, NestedSubmenus = true,
                HiddenShortcut = true, ShortcutLabels = true, CheckedCommand = true, OrdinaryCommand = true, DynamicWindows = true, LongTitles = true,
                OpenMenuResizeSafe = true, RapidResizeRestoration = true,
                OverflowGlyph = new { glyph.Glyph, glyph.FontSize, glyphBounds.Width, glyphBounds.Height, VerticalCenterOffset = glyphCenterOffset } };
        }
        finally
        {
            await CloseOverflowMenuAsync(window, overflow);
            if (originalCursor is { } restore && clickPoint is { } clicked && GetCursorPos(out var current)
                && current.X == clicked.X && current.Y == clicked.Y) SetCursorPos(restore.X, restore.Y);
            if (other is not null) await CloseWindowAsync(other);
            foreach (var pair in titles) pair.Key.Title = pair.Value;
            AppDefaults.Shared.UseColumnLayout = originalColumn;
            if (window.AppWindow.Presenter is OverlappedPresenter presenter) presenter.Restore();
            window.AppWindow.MoveAndResize(new(original.Bounds.X, original.Bounds.Y, original.Bounds.Width, original.Bounds.Height));
            content.ViewModel.RestoreWindowLayout(original);
            window.Activate();
            SetForegroundWindow(WindowNative.GetWindowHandle(window));
        }
    }
}
#endif
