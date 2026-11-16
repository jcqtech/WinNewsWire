#if PARITY_PROBE
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinNewsWire.AppRuntime;
using WinNewsWire.AppShared;
using WinNewsWire.Core;

namespace WinNewsWire.Diagnostics;

internal static partial class ParityProbe
{
    private static async Task<object?> CheckRenderingPreferenceAsync(MainWindow window, bool scrolling = false)
    {
        bool Read(AppDefaults defaults) => scrolling ? defaults.UseWheelInertia : defaults.UseSoftwareRasterization;
        void Write(bool value)
        {
            if (scrolling) AppDefaults.Shared.UseWheelInertia = value;
            else AppDefaults.Shared.UseSoftwareRasterization = value;
        }
        bool Running() => scrolling ? !WebViewEnvironment.UsesDirectScrolling : WebViewEnvironment.UsesSoftwareRasterization;
        bool original = Read(AppDefaults.Shared);
        bool running = Running();
        string toggleName = scrolling ? "WheelInertiaToggle" : "CompatibilityRenderingToggle";
        string statusName = scrolling ? "WheelInertiaStatus" : "CompatibilityRenderingStatus";
        string errorKey = scrolling ? "ScrollingSaveFailed" : "RenderingSaveFailed";
        var core = window.ProbeContent.ProbeWebView.CoreWebView2;
        string path = Path.Combine(AppConfig.DataDirectory, "AppDefaults.json");
        AppWindows.PreferencesWindow? preferences = null;
        MainWindow? other = null;
        async Task<ToggleSwitch> ShowRenderingOptions(AppWindows.PreferencesWindow preferencesWindow)
        {
            var expander = Descendants(preferencesWindow.Content).OfType<Expander>()
                .Single(control => control.Name == "AdvancedOptionsExpander");
            expander.IsExpanded = true;
            expander.StartBringIntoView();
            await WaitFor(() => Task.FromResult(Descendants(preferencesWindow.Content).OfType<ToggleSwitch>()
                .Any(control => control.Name == toggleName && control.IsLoaded)), "Expanded browser options");
            return Descendants(preferencesWindow.Content).OfType<ToggleSwitch>()
                .Single(control => control.Name == toggleName);
        }
        try
        {
            Require(original == running, "The saved rendering choice was not applied at startup.");
            preferences = new AppWindows.PreferencesWindow();
            preferences.Activate();
            await WaitFor(() => Task.FromResult(preferences.Content is FrameworkElement { IsLoaded: true }), "Rendering preferences");
            var toggle = await ShowRenderingOptions(preferences);
            var status = Descendants(preferences.Content).OfType<TextBlock>()
                .Single(control => control.Name == statusName);
            Require(toggle.IsOn == original, "The rendering control did not reflect the saved choice.");
            toggle.IsOn = !original;
            Require(Read(new AppDefaults(path)) == !original
                && status.Text == AppStrings.Get("RenderingRestartRequired"), "The rendering choice or restart notice was not persisted.");

            other = App.OpenMainWindow();
            await WaitFor(() => Task.FromResult(other.ProbeContent.IsLoaded && other.ProbeContent.ProbeWebViewReady), "Rendering companion window");
            Require(Running() == running
                && other.ProbeContent.ProbeWebView.CoreWebView2.BrowserProcessId == core.BrowserProcessId
                && other.ProbeContent.ProbeWebView.CoreWebView2.Environment.UserDataFolder == core.Environment.UserDataFolder,
                "A pending rendering change replaced the running environment or browser profile.");

            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                toggle.IsOn = original;
                Require(toggle.IsOn == !original && Read(AppDefaults.Shared) == !original,
                    "A failed rendering preference save changed the selected mode.");
            }
            Require(status.Text.StartsWith(AppStrings.Get(errorKey).Replace("{0}", "")),
                "The rendering preference failure was not shown.");
            toggle.IsOn = original;
            preferences.Close();
            preferences = new AppWindows.PreferencesWindow();
            preferences.Activate();
            await WaitFor(() => Task.FromResult(preferences.Content is FrameworkElement { IsLoaded: true }), "Reopened rendering preferences");
            Require((await ShowRenderingOptions(preferences)).IsOn == original,
                "Reopening preferences lost the rendering choice.");
            return new { SavedStartupMode = running, RestartRequired = true, SharedBrowser = true, ProfileRetained = true, FailedSaveRollback = true };
        }
        finally
        {
            preferences?.Close();
            if (other is not null) await CloseWindowAsync(other);
            if (Read(AppDefaults.Shared) != original) Write(original);
            window.Activate();
        }
    }
}
#endif
