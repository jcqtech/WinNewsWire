using System;
using System.Collections.Generic;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinNewsWire.Core;

namespace WinNewsWire.AppWindows;

/// <summary>
/// Pushes the user's <see cref="AppDefaults.AppearanceMode"/> selection
/// (System / Light / Dark) onto each top-level Window and ContentDialog so
/// pop-ups follow the same theme as the main window. Also doubles as a
/// secondary-window registry so the app can close every open pop-up cleanly
/// when the main window goes away.
/// </summary>
/// <remarks>
/// In WinUI 3 every <c>Window</c> has its own XAML tree, so the
/// <c>RequestedTheme</c> set on <c>MainWindow.Content</c> doesn't reach
/// secondary windows (About, ErrorLog, Preferences, etc.) — those would
/// otherwise just track Windows' system theme and ignore the user's
/// override. This helper makes them all consistent and re-applies the
/// theme when the user toggles it in the View > Appearance menu.
/// </remarks>
internal static class WindowThemeHelper
{
    // Tracks every secondary window that called Attach so they can all be
    // closed when MainWindow goes away. Modified only on the UI thread (each
    // Window's ctor + Closed handler run there) so a plain HashSet is safe.
    private static readonly HashSet<Window> _trackedWindows = new();

    /// <summary>Snapshot of every secondary window currently open.</summary>
    public static IReadOnlyCollection<Window> OpenSecondaryWindows
    {
        get
        {
            // Copy under no lock — the set is only mutated on the UI thread
            // and callers are always on the UI thread when iterating.
            return _trackedWindows.Count == 0
                ? Array.Empty<Window>()
                : new List<Window>(_trackedWindows);
        }
    }

    /// <summary>
    /// Apply the current AppDefaults appearance to <paramref name="window"/>'s
    /// root <see cref="FrameworkElement"/> and keep it in sync with future
    /// changes. Subscriber is auto-detached when the window closes.
    /// Also registers the window in the open-windows registry.
    /// </summary>
    public static void Attach(Window window)
    {
        if (window is null) return;

        ApplyToWindow(window);
        _trackedWindows.Add(window);

        EventHandler<string>? handler = null;
        handler = (_, _) => TryDispatch(window, () => ApplyToWindow(window));
        AppDefaults.Shared.Changed += handler;

        window.Closed += (_, _) =>
        {
            _trackedWindows.Remove(window);
            if (handler is not null)
            {
                AppDefaults.Shared.Changed -= handler;
                handler = null;
            }
        };
    }

    /// <summary>
    /// Closes every secondary window currently tracked. Used by
    /// <c>MainWindow.Closed</c> so the process tears down deterministically
    /// instead of leaking pop-up windows that keep the UI thread alive after
    /// the main window has shut down its services.
    /// </summary>
    public static void CloseAllSecondaryWindows()
    {
        // Snapshot first — Close() raises Closed which mutates _trackedWindows.
        var snapshot = new List<Window>(_trackedWindows);
        foreach (var w in snapshot)
        {
            try { w.Close(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WindowThemeHelper.CloseAll: {ex.Message}");
            }
        }
        _trackedWindows.Clear();
    }

    /// <summary>
    /// Apply the current AppDefaults appearance to a <see cref="ContentDialog"/>
    /// before it's shown. ContentDialogs that target a XamlRoot whose
    /// <c>RequestedTheme</c> is already correct don't need this, but the
    /// helper is safe to call unconditionally — it's just an idempotent set.
    /// </summary>
    public static void Apply(ContentDialog dialog)
    {
        if (dialog is null) return;
        dialog.RequestedTheme = CurrentElementTheme();
    }

    /// <summary>
    /// Fluent variant of <see cref="Apply(ContentDialog)"/> so dialog factories
    /// can chain: <c>new ContentDialog { ... }.WithCurrentTheme()</c>.
    /// </summary>
    public static ContentDialog WithCurrentTheme(this ContentDialog dialog)
    {
        Apply(dialog);
        return dialog;
    }

    private static void ApplyToWindow(Window window)
    {
        if (window.Content is FrameworkElement root)
            root.RequestedTheme = CurrentElementTheme();
    }

    private static ElementTheme CurrentElementTheme() => AppDefaults.Shared.AppearanceMode switch
    {
        AppDefaults.Appearance.Light => ElementTheme.Light,
        AppDefaults.Appearance.Dark  => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    private static void TryDispatch(Window window, Action action)
    {
        try
        {
            var queue = window.DispatcherQueue;
            if (queue is null || queue.HasThreadAccess) action();
            else queue.TryEnqueue(() => { try { action(); } catch { } });
        }
        catch { /* window may be torn down; ignore */ }
    }
}

