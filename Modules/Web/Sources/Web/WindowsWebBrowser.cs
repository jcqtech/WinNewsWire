using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace WinNewsWire.Web;

public sealed record BrowserLaunchRequest(Uri Url, string? BrowserPath, bool InBackground, bool SelectedBrowserUnavailable = false);

public sealed record WindowsWebBrowser(string Name, string ExecutablePath, string? RegistrationID = null)
{
    public string Identifier => ExecutablePath;
    public string DisplayPath => BrowserPathDisplay.ForCanonicalParentPath(Path.GetDirectoryName(ExecutablePath) ?? ExecutablePath);

    public static ISet<string> DuplicateBrowserNames(IEnumerable<WindowsWebBrowser> browsers)
        => browsers.GroupBy(browser => browser.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);

    public static IReadOnlyList<WindowsWebBrowser> SortBrowsers(IEnumerable<WindowsWebBrowser> browsers)
        => browsers.DistinctBy(browser => browser.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(browser => browser.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(browser => browser.DisplayPath, StringComparer.CurrentCultureIgnoreCase).ToArray();

    public static BrowserLaunchRequest CreateLaunchRequest(Uri url, string? selectedBrowser,
        IEnumerable<WindowsWebBrowser> browsers, bool inBackground)
    {
        var prepared = url.PreparedForOpeningInBrowser()
            ?? throw new ArgumentException("Only web and email links can be opened.", nameof(url));
        if (prepared.Scheme == "mailto" || string.IsNullOrEmpty(selectedBrowser))
            return new(prepared, null, inBackground);
        var browser = browsers.FirstOrDefault(candidate =>
            string.Equals(candidate.Identifier, selectedBrowser, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.RegistrationID, selectedBrowser, StringComparison.OrdinalIgnoreCase));
        string? path = browser?.ExecutablePath;
        if (path is null && Path.IsPathFullyQualified(selectedBrowser)
            && string.Equals(Path.GetExtension(selectedBrowser), ".exe", StringComparison.OrdinalIgnoreCase))
            path = selectedBrowser;
        if (path is not null && File.Exists(path)) return new(prepared, path, inBackground);
        Trace.TraceWarning("The selected browser is unavailable; using the system default.");
        return new(prepared, null, inBackground, SelectedBrowserUnavailable: true);
    }

    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<WindowsWebBrowser> SortedBrowsers()
    {
        var browsers = new List<WindowsWebBrowser>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var clients = root.OpenSubKey(@"SOFTWARE\Clients\StartMenuInternet");
                if (clients is null) continue;
                foreach (var id in clients.GetSubKeyNames())
                {
                    using var client = clients.OpenSubKey(id);
                    using var urls = client?.OpenSubKey(@"Capabilities\URLAssociations");
                    using var files = client?.OpenSubKey(@"Capabilities\FileAssociations");
                    if (urls?.GetValue("https") is not string || files?.GetValue(".html") is not string) continue;
                    using var command = client?.OpenSubKey(@"shell\open\command");
                    if (command?.GetValue(null) is not string text || string.IsNullOrWhiteSpace(text)) continue;
                    var path = ExecutableFromCommand(Environment.ExpandEnvironmentVariables(text));
                    if (path is null || !File.Exists(path) || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    using var capabilities = client?.OpenSubKey("Capabilities");
                    var name = client?.GetValue(null) as string ?? capabilities?.GetValue("ApplicationName") as string;
                    if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(path);
                    if (name.StartsWith("@", StringComparison.Ordinal))
                    {
                        var buffer = new StringBuilder(1024);
                        int result = SHLoadIndirectString(name, buffer, (uint)buffer.Capacity, IntPtr.Zero);
                        if (result >= 0) name = buffer.ToString();
                        else
                        {
                            Trace.TraceWarning($"Could not resolve browser display name for {id}: 0x{result:X8}.");
                            name = Path.GetFileNameWithoutExtension(path);
                        }
                    }
                    browsers.Add(new(name, Path.GetFullPath(path), id));
                }
            }
            catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                Trace.TraceWarning($"Could not enumerate browsers in {hive}/{view}: {error}");
            }
        }
        return SortBrowsers(browsers);
    }

    [SupportedOSPlatform("windows")]
    public static void Open(BrowserLaunchRequest request, IntPtr owner = default)
    {
        var prepared = request.Url.PreparedForOpeningInBrowser()
            ?? throw new ArgumentException("Only web and email links can be opened.", nameof(request));
        var info = new ShellExecuteInfo
        {
            Size = Marshal.SizeOf<ShellExecuteInfo>(),
            Mask = 0x00000400 | 0x00000100,
            Window = owner,
            Verb = "open",
            File = request.BrowserPath ?? prepared.AbsoluteUri,
            Parameters = request.BrowserPath is null ? null : QuoteArgument(prepared.AbsoluteUri),
            Show = request.InBackground ? 4 : 1
        };
        if (!ShellExecuteExW(ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character);
            slashes = 0;
        }
        result.Append('\\', slashes * 2);
        return result.Append('"').ToString();
    }

    [SupportedOSPlatform("windows")]
    private static string? ExecutableFromCommand(string command)
        => Core.WindowsCommandLine.Split(command).FirstOrDefault();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int Size;
        public uint Mask;
        public IntPtr Window;
        public string? Verb;
        public string? File;
        public string? Parameters;
        public string? Directory;
        public int Show;
        public IntPtr Instance;
        public IntPtr IDList;
        public string? Class;
        public IntPtr ClassKey;
        public uint HotKey;
        public IntPtr Icon;
        public IntPtr Process;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteExW(ref ShellExecuteInfo info);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, StringBuilder output, uint length, IntPtr reserved);
}
