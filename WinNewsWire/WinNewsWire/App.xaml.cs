using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinNewsWire
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;
        public static Window? MainWindow => (Application.Current as App)?._window;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            // Subscribe BEFORE InitializeComponent so a failure parsing App.xaml is captured.
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            AppDomain.CurrentDomain.FirstChanceException += CurrentDomain_FirstChanceException;
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            LogException("App.ctor", null, "constructor entered");

            try { InitializeComponent(); }
            catch (Exception ex)
            {
                LogException("App.InitializeComponent", ex);
                throw;
            }

            UnhandledException += App_UnhandledException;
            LogException("App.ctor", null, "constructor exited cleanly");
        }

        private static int _firstChanceDepth;
        private void CurrentDomain_FirstChanceException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            // Re-entrancy guard: writing to disk can itself throw, which would loop forever.
            if (System.Threading.Interlocked.Increment(ref _firstChanceDepth) > 1)
            {
                System.Threading.Interlocked.Decrement(ref _firstChanceDepth);
                return;
            }
            try
            {
                // Skip extremely common benign exceptions to avoid log spam.
                var t = e.Exception.GetType().FullName ?? "";
                if (t == "System.IO.FileNotFoundException" || t == "System.OperationCanceledException")
                    return;
                LogException("FirstChance", e.Exception, t);
            }
            finally { System.Threading.Interlocked.Decrement(ref _firstChanceDepth); }
        }

        private static readonly string CrashLogPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinNewsWire",
            "crash.log");

        private static void LogException(string source, Exception? ex, string? message = null)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CrashLogPath)!);
                var text = $"[{DateTime.Now:O}] {source}: {message}{Environment.NewLine}{ex}{Environment.NewLine}{new string('-', 80)}{Environment.NewLine}";
                File.AppendAllText(CrashLogPath, text);
                System.Diagnostics.Debug.WriteLine(text);
                System.Diagnostics.Debugger.Log(0, source, text);
            }
            catch { }
        }

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            LogException("XAML.UnhandledException", e.Exception, e.Message);
            // Break into the debugger when attached so we can inspect the real exception
            // instead of the opaque 0xC000027B failfast.
            if (System.Diagnostics.Debugger.IsAttached)
                System.Diagnostics.Debugger.Break();
            // Leave e.Handled = false so failfast still occurs in production.
        }

        private void CurrentDomain_UnhandledException(object sender, System.UnhandledExceptionEventArgs e)
            => LogException("AppDomain.UnhandledException", e.ExceptionObject as Exception, $"IsTerminating={e.IsTerminating}");

        private void TaskScheduler_UnobservedTaskException(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
        {
            LogException("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            try { WinNewsWire.AppRuntime.AppService.Shared.Start(); }
            catch (Exception ex) { LogException("AppService.Start", ex); }

            try
            {
                _window = new MainWindow();
                _window.Closed += MainWindow_Closed;
                _window.Activate();
            }
            catch (Exception ex)
            {
                LogException("OnLaunched", ex);
                throw;
            }
        }

        private bool _shuttingDown;

        /// <summary>
        /// When the main window closes we must (a) tear down any pop-up
        /// windows that are still open, (b) stop the AppService timers
        /// before XAML tries to dispatch a callback onto an unrooted
        /// window, and (c) explicitly Exit the application — otherwise
        /// secondary windows can keep the message loop alive while their
        /// dependencies (timers, notifier, WebView2) are already torn down,
        /// which surfaces as a non-zero process exit code.
        /// </summary>
        private void MainWindow_Closed(object sender, Microsoft.UI.Xaml.WindowEventArgs args)
        {
            if (_shuttingDown) return;
            _shuttingDown = true;

            // Close the WebView2 before tearing down AppService / the
            // UI thread to avoid a winrt::hresult_error (0x8007139F)
            // raised by the CoreWebView2 during process shutdown.
            try { (_window as MainWindow)?.ShutdownContent(); }
            catch (Exception ex) { LogException("MainContent.Shutdown", ex); }

            // Close any About / Preferences / ErrorLog / Feedback /
            // KeyboardShortcuts windows the user left open so they don't
            // keep the process alive (or crash) after AppService is gone.
            try { WinNewsWire.AppWindows.WindowThemeHelper.CloseAllSecondaryWindows(); }
            catch (Exception ex) { LogException("CloseAllSecondaryWindows", ex); }

            try { WinNewsWire.AppRuntime.AppService.Shared.Stop(); }
            catch (Exception ex) { LogException("AppService.Stop", ex); }

            // Force exit so the runtime doesn't sit waiting for any
            // straggling popup or background coroutine that referenced
            // resources we just disposed.
            try { Exit(); }
            catch (Exception ex) { LogException("Application.Exit", ex); }
        }
    }
}
