using System.IO;

namespace WinNewsWire.Core;

public static class AppConfig
{
    public const string AppName = "WinNewsWire";

    private static string? _dataDirectoryOverride;

    private static readonly Lazy<string> _defaultDataDirectory = new(() =>
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(root, AppName);
        Directory.CreateDirectory(dir);
        return dir;
    });

    public static string DataDirectory
    {
        get
        {
            if (_dataDirectoryOverride is { } o)
            {
                Directory.CreateDirectory(o);
                return o;
            }
            return _defaultDataDirectory.Value;
        }
    }

    /// <summary>Test-only hook: redirects <see cref="DataDirectory"/> (and therefore every
    /// derived subdirectory such as <see cref="AccountsDirectory"/>) to a caller-supplied path,
    /// so tests don't pollute the real per-user data folder. Pass <c>null</c> to restore the
    /// default <c>%LOCALAPPDATA%\WinNewsWire</c> location.</summary>
    public static void SetDataDirectoryOverride(string? path) => _dataDirectoryOverride = path;

    public static string EnsureSubdirectory(string name)
    {
        var p = Path.Combine(DataDirectory, name);
        Directory.CreateDirectory(p);
        return p;
    }

    public static string AccountsDirectory => EnsureSubdirectory("Accounts");
    public static string CachesDirectory => EnsureSubdirectory("Caches");
    public static string FaviconsDirectory => EnsureSubdirectory("Favicons");
    public static string ThemesDirectory => EnsureSubdirectory("Themes");
    public static string LogsDirectory => EnsureSubdirectory("Logs");
}
