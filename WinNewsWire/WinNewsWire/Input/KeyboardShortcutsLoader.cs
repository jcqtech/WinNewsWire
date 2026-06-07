using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace WinNewsWire.Input;

public enum KeyboardScope { Global, Sidebar, Timeline, Detail }

/// <summary>Parsed entry from one of the 4 NNW keyboard-shortcut plist files.</summary>
public sealed record KeyboardShortcutDef(
    string Key,
    string Action,
    string? Title,
    bool ShiftModifier,
    bool CommandModifier,
    bool OptionModifier,
    KeyboardScope Scope);

/// <summary>Parses NetNewsWire's 4 keyboard-shortcut plist files (Global/Sidebar/Timeline/Detail)
/// verbatim. Backs both the keyboard-shortcuts cheat-sheet window and the runtime accelerator
/// registration on MainContent.</summary>
public static class KeyboardShortcutsLoader
{
    public static IReadOnlyList<KeyboardShortcutDef> LoadAll()
    {
        var list = new List<KeyboardShortcutDef>();
        foreach (var (file, scope) in new (string, KeyboardScope)[]
                 {
                     ("GlobalKeyboardShortcuts.plist", KeyboardScope.Global),
                     ("SidebarKeyboardShortcuts.plist", KeyboardScope.Sidebar),
                     ("TimelineKeyboardShortcuts.plist", KeyboardScope.Timeline),
                     ("DetailKeyboardShortcuts.plist", KeyboardScope.Detail),
                 })
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Keyboard", file);
            if (!File.Exists(path)) continue;
            list.AddRange(ParseFile(path, scope));
        }
        return list;
    }

    private static IEnumerable<KeyboardShortcutDef> ParseFile(string path, KeyboardScope scope)
    {
        XDocument doc;
        try { doc = XDocument.Load(path); } catch { yield break; }

        var root = doc.Descendants("array").FirstOrDefault();
        if (root is null) yield break;

        foreach (var dict in root.Elements("dict"))
        {
            string? key = null, action = null, title = null;
            bool shift = false, cmd = false, option = false;
            XElement? pendingKey = null;
            foreach (var el in dict.Elements())
            {
                if (el.Name == "key") { pendingKey = el; continue; }
                if (pendingKey is null) continue;
                switch (pendingKey.Value)
                {
                    case "key": key = el.Value; break;
                    case "action": action = el.Value; break;
                    case "title": title = el.Value; break;
                    case "shiftModifier": shift = el.Name.LocalName == "true"; break;
                    case "commandModifier": cmd = el.Name.LocalName == "true"; break;
                    case "optionModifier": option = el.Name.LocalName == "true"; break;
                }
                pendingKey = null;
            }
            if (key is not null && action is not null)
                yield return new KeyboardShortcutDef(key, action, title, shift, cmd, option, scope);
        }
    }

    /// <summary>Human-readable chord for the cheat-sheet window, e.g. <c>[uparrow]</c> + shift ⇒ "Shift + ↑".</summary>
    public static string FormatChord(KeyboardShortcutDef def)
    {
        var parts = new List<string>();
        if (def.CommandModifier) parts.Add("Ctrl");
        if (def.OptionModifier) parts.Add("Alt");
        if (def.ShiftModifier) parts.Add("Shift");
        parts.Add(FormatKey(def.Key));
        return string.Join(" + ", parts);
    }

    public static string FormatKey(string k) => k switch
    {
        "[space]" => "Space",
        "[tab]" => "Tab",
        "[return]" => "Enter",
        "[enter]" => "Enter",
        "[uparrow]" => "↑",
        "[downarrow]" => "↓",
        "[leftarrow]" => "←",
        "[rightarrow]" => "→",
        "[delete]" => "Backspace",
        "[deletefunction]" => "Delete",
        "[escape]" => "Esc",
        "[home]" => "Home",
        "[end]" => "End",
        _ => k.ToUpperInvariant(),
    };
}
