using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using WinNewsWire.Core;

namespace WinNewsWire.AppRuntime;

internal static class WebViewEnvironment
{
    private static Task<CoreWebView2Environment>? _environment;
    internal static bool UsesSoftwareRasterization { get; }
    internal static bool UsesDirectScrolling { get; }
#if PARITY_PROBE
    internal static string? ProbeBrowserArguments;
#endif

    static WebViewEnvironment()
    {
        UsesSoftwareRasterization = AppDefaults.Shared.UseSoftwareRasterization;
        UsesDirectScrolling = AppDefaults.Shared.UseDirectBrowserScrolling;
    }

    internal static Task<CoreWebView2Environment> GetAsync()
    {
        if (_environment is null || _environment.IsFaulted || _environment.IsCanceled)
            _environment = CreateAsync();
        return _environment;
    }

    private static async Task<CoreWebView2Environment> CreateAsync()
    {
        var options = new CoreWebView2EnvironmentOptions();
        var arguments = new List<string>();
        var languages = global::Windows.Globalization.ApplicationLanguages.Languages;
        if (languages.Count > 0) options.Language = languages[0];
        if (UsesSoftwareRasterization)
        {
            // Opt-in workaround for blocking ANGLE shader compilation; video and compositing still use the GPU.
            arguments.Add("--disable-gpu-rasterization");
            App.LogException("WebView2 rendering", null, "Experimental software page rasterization requested.");
        }
        if (UsesDirectScrolling)
        {
            arguments.Add("--disable-smooth-scrolling");
            App.LogException("WebView2 input", null, "Experimental direct browser scrolling requested.");
        }
#if PARITY_PROBE
        if (Array.Exists(Environment.GetCommandLineArgs(), argument => argument == "--inertial-scrolling"))
        {
            arguments.Add("--remote-debugging-port=0");
            arguments.Add("--remote-debugging-address=127.0.0.1");
        }
#endif
        options.AdditionalBrowserArguments = string.Join(" ", arguments);
#if PARITY_PROBE
        ProbeBrowserArguments = options.AdditionalBrowserArguments;
#endif
        string userDataFolder = "";
#if PARITY_PROBE
        userDataFolder = System.IO.Path.Combine(AppConfig.CachesDirectory, "WebView2");
#endif
        return await CoreWebView2Environment.CreateWithOptionsAsync("", userDataFolder, options);
    }
}
