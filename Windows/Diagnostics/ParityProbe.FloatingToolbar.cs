#if PARITY_PROBE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinNewsWire.Account;
using WinNewsWire.Core;
using Windows.Foundation;
using WinRT.Interop;

namespace WinNewsWire.Diagnostics;

internal static partial class ParityProbe
{
    private sealed record FloatingToolbarFrame(double Opacity, double Offset);

    private static async Task<object?> CheckFloatingToolbarRevealAsync(MainWindow window)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        await content.Initialization.WaitAsync(TimeSpan.FromSeconds(30));
        var original = window.CaptureState();
        var configuration = content.CurrentToolbarConfiguration;
        bool column = AppDefaults.Shared.UseColumnLayout;
        var surface = (Border)content.FindName("FloatingToolbarSurface");
        var button = (Button)content.FindName("FloatingToolbarButton");
        var menu = (MenuFlyout)button.Flyout;
        var root = (UIElement)window.Content;
        var handle = WindowNative.GetWindowHandle(window);
        Require(GetCursorPos(out var originalCursor), "Could not save the pointer position.");
        CapturePoint? lastPointer = null;
        AppWindows.PreferencesWindow? preferences = null;
        var motion = new Dictionary<string, List<FloatingToolbarFrame>>();
        int pointerMoves = 0;
        PointerEventHandler moved = (_, _) => pointerMoves++;
        content.AddHandler(UIElement.PointerMovedEvent, moved, handledEventsToo: true);
        Rect Bounds(FrameworkElement element) => element.TransformToVisual(root)
            .TransformBounds(new(0, 0, element.ActualWidth, element.ActualHeight));
        double Offset() => (surface.RenderTransform as TranslateTransform)?.X ?? 0;
        DispatcherQueueTimer PointerTimer() => typeof(MainContent)
            .GetField("_floatingToolbarPointerTimer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(content) as DispatcherQueueTimer
            ?? throw new InvalidOperationException("The floating toolbar pointer tracker is unavailable.");
        void Move(Point point)
        {
            var origin = new CapturePoint();
            Require(ClientToScreen(handle, ref origin), "Could not locate the private hover window.");
            double scale = content.XamlRoot.RasterizationScale;
            var screen = new CapturePoint { X = origin.X + (int)Math.Round(point.X * scale),
                Y = origin.Y + (int)Math.Round(point.Y * scale) };
            Require(GetForegroundWindow() == handle && GetAncestor(WindowFromPoint(screen), 2) == handle,
                "The private hover window does not own the pointer target.");
            Require(SetCursorPos(screen.X, screen.Y), "Could not move the pointer within the private hover window.");
            lastPointer = screen;
        }
        async Task Sample(string name, Point point)
        {
            var frames = new List<FloatingToolbarFrame>();
            motion.Add(name, frames);
            Move(point);
            for (int index = 0; index < 14; index++)
            {
                frames.Add(new(surface.Opacity, Offset()));
                await Task.Delay(20);
            }
            File.WriteAllText(Path.Combine(OutputDirectory, "floating-toolbar-motion.json"),
                JsonSerializer.Serialize(motion, new JsonSerializerOptions { WriteIndented = true }));
        }
        try
        {
            AppDefaults.Shared.UseColumnLayout = false;
            model.IsUnifiedLayout = false;
            var account = AccountManager.Shared.DefaultAccount;
            var feed = account.FlattenedFeeds().Single();
            model.SelectedSidebarItem = model.FindSidebarItemForFeed(account.AccountID, feed.FeedID);
            await model.FilterArticlesAsync();
            await model.WaitForCurrentTimelineAsync(CancellationToken.None);
            model.SelectedArticle = model.ArticleItems.Single(item => item.Id == "probe-article");
            await WaitFor(() => Task.FromResult(content.ProbeArticleReady), "Hover fixture reader document");
            Require(content.SaveToolbarConfiguration(configuration.WithVisibility(false)) is null,
                "Could not hide the toolbar for the hover fixture.");
            if (Environment.GetCommandLineArgs().Contains("--floating-toolbar-away"))
            {
                var list = (ListView)content.FindName("ArticleListView");
                Require(list.Focus(FocusState.Programmatic), "Could not leave the floating button unfocused.");
                await SettleAppearanceAsync(window, list);
                await WaitForRenderedFramesAsync();
                var origin = new CapturePoint();
                Require(ClientToScreen(handle, ref origin), "Could not locate the reader's client area.");
                Require(GetCursorPos(out var cursor), "Could not read the current cursor position without moving it.");
                double scale = content.XamlRoot.RasterizationScale;
                var point = new Point((cursor.X - origin.X) / scale, (cursor.Y - origin.Y) / scale);
                var restingBounds = Bounds(button);
                restingBounds.X -= Offset();
                var vicinity = new Rect(restingBounds.Left - 48, restingBounds.Top - 48,
                    restingBounds.Width + 96, restingBounds.Height + 96);
                Require(!vicinity.Contains(point), "The current cursor is near the button; the non-injected away control cannot run.");
                await Task.Delay(250);
                File.WriteAllText(Path.Combine(OutputDirectory, "floating-toolbar-away.json"),
                    JsonSerializer.Serialize(new { Pointer = point, Vicinity = vicinity, surface.Opacity,
                        Offset = Offset(), button.FocusState, Foreground = GetForegroundWindow() == handle }));
                Require(surface.Opacity == 0 && Offset() > 0,
                    "The floating toolbar remains visible with an unfocused button and a distant mouse.");
                if (GetForegroundWindow() != handle)
                    Require(!PointerTimer().IsRunning, "Inactive windows leave proximity polling running.");
                Require(content.SaveToolbarConfiguration(configuration.WithVisibility(true)) is null,
                    "Could not restore the full toolbar.");
                Require(!PointerTimer().IsRunning, "Visible toolbars leave unnecessary proximity polling running.");
                return new { ObservedDistantMouse = true, NoInputInjected = true, Hidden = true, NoUnneededPolling = true };
            }
            window.Activate();
            SetForegroundWindow(handle);
            await WaitFor(() => Task.FromResult(GetForegroundWindow() == handle), "Owned hover input");
            Require(button.Focus(FocusState.Keyboard), "The floating options button cannot receive keyboard focus.");
            await WaitFor(() => Task.FromResult(surface.Opacity == 1 && Offset() == 0),
                "Keyboard focus reveals the floating toolbar");
            double rightInset = Bounds(content).Right - Bounds(button).Right;
            var bounds = Bounds(button);
            var reader = Bounds(content.ProbeWebView);
            var away = new Point(reader.Left + 60, reader.Top + Math.Min(140, reader.Height / 2));
            var near = new Point(bounds.Left - 24, bounds.Top + bounds.Height / 2);
            Require(near.X >= reader.Left && near.Y >= reader.Top && !bounds.Contains(near),
                "The hover fixture must approach through reader content, not the button itself.");

            await Sample("out", away);
            Require(surface.Opacity == 0 && Offset() > 0,
                "The floating toolbar remains visible when the mouse is away. Native WinUI pointer moves=" + pointerMoves
                + ", focus=" + button.FocusState + ", pointer=" + typeof(MainContent).GetField("_floatingToolbarPointerPosition",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(content));
            await Sample("in", near);
            Require(surface.Opacity == 1 && Offset() == 0,
                "Mouse proximity through the reader does not reveal the floating toolbar.");
            Require(Math.Abs(rightInset - 28) < 1, "The floating toolbar has not moved slightly right.");
            if (new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
            {
                Require(motion["out"].Any(frame => frame.Opacity is > 0 and < 1 && frame.Offset > 0)
                    && motion["in"].Any(frame => frame.Opacity is > 0 and < 1 && frame.Offset > 0),
                    "The floating toolbar does not fade and slide between its resting positions.");
            }
            var performance = content.ProbeToolbarPerformance;
            for (int index = 0; index < 4; index++)
            {
                Move(new(near.X + index, near.Y));
                await Task.Delay(25);
            }
            var after = content.ProbeToolbarPerformance;
            Require(after.Layout == performance.Layout && after.Presentation == performance.Presentation,
                "Pointer proximity repeatedly relaid out the command headers.");

            Move(away);
            await Task.Delay(40);
            Move(near);
            await WaitFor(() => Task.FromResult(surface.Opacity == 1 && Offset() == 0),
                "A reversed hover animation settles fully visible");
            await ClickElementAsync(button, () => menu.Items.Count > 0 && menu.Items[0].IsLoaded,
                "Open floating toolbar options");
            Move(away);
            await Task.Delay(220);
            Require(surface.Opacity == 1 && Offset() == 0, "The floating button disappeared while its menu was open.");
            menu.Hide();
            await WaitFor(() => Task.FromResult(surface.Opacity == 0),
                "Closing the menu restores mouse-proximity hiding");
            Require(button.Focus(FocusState.Keyboard), "The hidden floating toolbar lost keyboard access.");
            await WaitFor(() => Task.FromResult(surface.Opacity == 1 && Offset() == 0),
                "Keyboard focus restores the faded button");
            Require(PointerTimer().IsRunning, "The active hidden-toolbar window stopped tracking pointer proximity.");
            preferences = new AppWindows.PreferencesWindow();
            preferences.Activate();
            await WaitFor(() => Task.FromResult(!PointerTimer().IsRunning && surface.Opacity == 0),
                "Inactive windows stop proximity tracking");
            preferences.Close();
            preferences = null;
            window.Activate();
            SetForegroundWindow(handle);
            await WaitFor(() => Task.FromResult(GetForegroundWindow() == handle && PointerTimer().IsRunning),
                "Reactivating the reader restores proximity tracking");
            Require(content.SaveToolbarConfiguration(configuration.WithVisibility(true)) is null,
                "Could not restore the full toolbar.");
            await WaitFor(() => Task.FromResult(surface.Visibility == Visibility.Collapsed),
                "The floating surface is absent when the toolbar is visible");
            Require(!PointerTimer().IsRunning, "Visible toolbars leave unnecessary proximity polling running.");
            return new { MovedRight = true, OwnedNativeMouse = true, ReaderProximity = true,
                FadeAndDirectionalSlide = true, Reversal = true, MenuStaysVisible = true, KeyboardAccess = true,
                NoPointerDrivenHeaderLayouts = true, NoInactiveOrVisibleToolbarPolling = true };
        }
        finally
        {
            content.RemoveHandler(UIElement.PointerMovedEvent, moved);
            menu.Hide();
            preferences?.Close();
            if (lastPointer is { } last && GetCursorPos(out var current) && current.X == last.X && current.Y == last.Y)
                SetCursorPos(originalCursor.X, originalCursor.Y);
            content.SaveToolbarConfiguration(configuration);
            AppDefaults.Shared.UseColumnLayout = column;
            await content.RestoreStateAsync(original);
        }
    }
}
#endif
