using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using WinNewsWire.Core;
using WinNewsWire.Web;

namespace WinNewsWire;

internal static class Browser
{
    internal static string InvertedOpenTitle => AppDefaults.Shared.OpenInBrowserInBackground
        ? "Open in Browser in Foreground" : "Open in Browser in Background";
#if PARITY_PROBE
    internal static IReadOnlyList<WindowsWebBrowser>? ProbeBrowsers { get; set; }
    internal static Action<BrowserLaunchRequest>? ProbeLaunch { get; set; }
#endif

    internal static IReadOnlyList<WindowsWebBrowser> SortedBrowsers()
    {
#if PARITY_PROBE
        if (ProbeBrowsers is { } browsers) return WindowsWebBrowser.SortBrowsers(browsers);
#endif
        return WindowsWebBrowser.SortedBrowsers();
    }

    internal static async Task<bool> OpenAsync(Uri url, IntPtr owner = default, bool invertPreference = false)
    {
        var prepared = url.PreparedForOpeningInBrowser()
            ?? throw new ArgumentException("Only web, email, and magnet links are supported.", nameof(url));
        bool background = AppDefaults.Shared.OpenInBrowserInBackground ^ invertPreference;
        var request = WindowsWebBrowser.CreateLaunchRequest(prepared, AppDefaults.Shared.DefaultBrowserID, SortedBrowsers(), background);
#if PARITY_PROBE
        if (ProbeLaunch is { } launch) { launch(request); return request.SelectedBrowserUnavailable; }
#endif
        if (prepared.Scheme is "http" or "https")
        {
            try
            {
                var handlers = await global::Windows.System.Launcher.FindAppUriHandlersAsync(prepared);
                if (handlers.Count > 0)
                {
                    if (await global::Windows.System.Launcher.LaunchUriAsync(prepared)) return false;
                    Trace.TraceWarning("The web link's registered application did not open it; trying the browser.");
                }
            }
            catch (Exception error) when (error is System.Runtime.InteropServices.COMException or ArgumentException)
            {
                Trace.TraceWarning("Could not open the link in its registered application; trying the browser: " + error.Message);
            }
        }
        WindowsWebBrowser.Open(request, owner);
        return request.SelectedBrowserUnavailable;
    }
}
