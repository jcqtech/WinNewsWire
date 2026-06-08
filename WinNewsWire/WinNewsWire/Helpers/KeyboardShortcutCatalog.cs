using System.Collections.Generic;
using System.Linq;
using Windows.System;

namespace WinNewsWire.Helpers;

/// <summary>
/// Single source-of-truth for the keyboard shortcuts displayed in
/// <c>KeyboardShortcutsWindow</c>. Each entry is grouped by section and rendered
/// dynamically so adding a shortcut requires only a single edit here (plus the
/// matching <c>KeyboardAccelerator</c> in XAML).
/// </summary>
public static class KeyboardShortcutCatalog
{
    public sealed record Shortcut(string KeyCombo, string Description);
    public sealed record Section(string Title, IReadOnlyList<Shortcut> Shortcuts);

    public static IReadOnlyList<Section> All { get; } = new List<Section>
    {
        new("Navigation", new Shortcut[]
        {
            new("J  /  Ctrl+\u2193", "Next article"),
            new("K  /  Ctrl+\u2191", "Previous article"),
            new("Space", "Scroll article / next unread"),
            new("Ctrl+Shift+B", "Toggle sidebar"),
        }),
        new("Article actions", new Shortcut[]
        {
            new("M  /  U", "Toggle read / unread"),
            new("S  /  L  /  Ctrl+L", "Toggle starred"),
            new("Ctrl+K", "Mark all as read"),
            new("Ctrl+B", "Open in browser"),
            new("Ctrl+Shift+W", "Toggle reader view"),
        }),
        new("Feeds", new Shortcut[]
        {
            new("Ctrl+N", "New feed"),
            new("Ctrl+Shift+N", "New folder"),
            new("R  /  Ctrl+R", "Refresh all feeds"),
            new("Ctrl+I", "Get info (Inspector)"),
        }),
        new("Window & app", new Shortcut[]
        {
            new("Ctrl+F", "Focus search"),
            new("Ctrl+M", "Minimize window"),
            new("Ctrl+W", "Close window"),
            new("Ctrl+Q", "Quit WinNewsWire"),
            new("Alt", "Reveal menu bar mnemonics"),
        }),
        new("Editing", new Shortcut[]
        {
            new("Ctrl+Z  /  Ctrl+Shift+Z", "Undo / redo"),
            new("Ctrl+X  /  C  /  V", "Cut / copy / paste"),
            new("Ctrl+A", "Select all (in text fields)"),
        }),
    };

    /// <summary>Format a <see cref="VirtualKey"/> + modifiers combo into a
    /// display string ("Ctrl+Shift+W", "F5", etc.). Used by diagnostic
    /// enumeration of the live accelerators.</summary>
    public static string FormatCombo(VirtualKey key, VirtualKeyModifiers mods)
    {
        var parts = new List<string>(4);
        if ((mods & VirtualKeyModifiers.Control) != 0) parts.Add("Ctrl");
        if ((mods & VirtualKeyModifiers.Shift) != 0) parts.Add("Shift");
        if ((mods & VirtualKeyModifiers.Menu) != 0) parts.Add("Alt");
        if ((mods & VirtualKeyModifiers.Windows) != 0) parts.Add("Win");
        parts.Add(KeyLabel(key));
        return string.Join("+", parts);
    }

    private static string KeyLabel(VirtualKey key) => key switch
    {
        VirtualKey.Up => "\u2191",
        VirtualKey.Down => "\u2193",
        VirtualKey.Left => "\u2190",
        VirtualKey.Right => "\u2192",
        VirtualKey.Space => "Space",
        VirtualKey.Enter => "Enter",
        VirtualKey.Escape => "Esc",
        VirtualKey.Tab => "Tab",
        _ => key.ToString(),
    };
}
