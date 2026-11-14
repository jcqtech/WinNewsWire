using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
using WinNewsWire.Models;

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
        private readonly List<MainWindow> _mainWindows = new();
        private readonly HashSet<MainWindow> _closingMainWindows = new();
        private readonly HashSet<MainWindow> _allowNativeClose = new();
        private readonly SemaphoreSlim _windowCloseGate = new(1, 1);
        private bool _startupDiagnosticsScheduled;
        private readonly bool _runtimeFirstChanceDiagnostics =
            Environment.GetEnvironmentVariable("WINNEWSWIRE_TRACE_FIRST_CHANCE") == "1";
        public static Window? MainWindow => (Application.Current as App)?._window;
        internal static string DisplayVersion => typeof(App).Assembly.GetName().Version?.ToString()
            ?? throw new InvalidOperationException("The application version is missing.");
        public static bool IsShuttingDown => Application.Current is App app && (app._shuttingDown || app._quitRequested);
        public static IReadOnlyList<MainWindow> MainWindows => Application.Current is App app
            ? app._mainWindows.Where(window => !app._closingMainWindows.Contains(window)).ToArray() : Array.Empty<MainWindow>();

        public static MainWindow OpenMainWindow(MainWindowState? state = null, bool activate = true)
            => (Application.Current as App ?? throw new InvalidOperationException("The application is not running.")).CreateMainWindow(activate, state);

        public static Window? WindowForXamlRoot(XamlRoot root)
            => MainWindows.FirstOrDefault(window => ReferenceEquals((window.Content as FrameworkElement)?.XamlRoot, root));

        public static async Task<bool> QuitAsync()
        {
            if (Application.Current is not App app || app._shuttingDown || app._quitRequested) return false;
            MainWindow[] windows;
            await app._windowCloseGate.WaitAsync();
            try
            {
                if (app._shuttingDown || app._quitRequested) return false;
                app._quitRequested = true;
                windows = MainWindows.ToArray();
                if (!await RefreshWindowScrollPositionsAsync(windows) || !TrySaveMainWindows(windows))
                {
                    app._quitRequested = false;
                    return false;
                }
            }
            finally { app._windowCloseGate.Release(); }
            foreach (var window in windows) await app.CloseMainWindowCoreAsync(window);
            return true;
        }

        public static Task<bool> CloseMainWindowAsync(MainWindow window)
            => (Application.Current as App ?? throw new InvalidOperationException("The application is not running."))
                .CloseMainWindowCoreAsync(window);

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
#if PARITY_PROBE
            Core.AppConfig.SetDataDirectoryOverride(System.IO.Path.Combine(Diagnostics.ParityProbe.OutputDirectory, "profile"));
            if (Array.Exists(Environment.GetCommandLineArgs(), argument => argument == "--network-startup"))
                Diagnostics.ParityProbe.ConfigureNetworkStartupCheck();
#endif
            WinNewsWire.Account.Account.LocalRefresherFactory = account => new WinNewsWire.Account.LocalAccountRefreshProcess(
                account, System.IO.Path.Combine(AppContext.BaseDirectory, "RefreshWorker", "WinNewsWire.RefreshWorker.exe"));
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
                if (e.Exception is FileNotFoundException or OperationCanceledException)
                    return;
                LogException("FirstChance", e.Exception, e.Exception.GetType().FullName);
            }
            finally { System.Threading.Interlocked.Decrement(ref _firstChanceDepth); }
        }

        private static string CrashLogPath => System.IO.Path.Combine(Core.AppConfig.DataDirectory, "crash.log");

#if PARITY_PROBE
        internal static int ProbeFirstChanceLogWrites;
        internal static int ProbeExplicitLogWrites;
#endif
        internal static void LogException(string source, Exception? ex, string? message = null)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CrashLogPath)!);
                var text = $"[{DateTime.Now:O}] {source}: {message}{Environment.NewLine}{ex}{Environment.NewLine}{new string('-', 80)}{Environment.NewLine}";
                File.AppendAllText(CrashLogPath, text);
#if PARITY_PROBE
                if (source == "FirstChance") Interlocked.Increment(ref ProbeFirstChanceLogWrites);
                else Interlocked.Increment(ref ProbeExplicitLogWrites);
#endif
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
        protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
#if PARITY_PROBE
            var probeWindow = CreateMainWindow(activate: false);
            await Diagnostics.ParityProbe.Run(probeWindow);
            return;
#else
            if (!await RegisterActivationAsync()) return;
            try { WinNewsWire.AppRuntime.AppService.Shared.Start(); }
            catch (Exception ex) { LogException("AppService.Start", ex); }

            try
            {
                _initialActivation ??= Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
                OpenSavedMainWindows();
                if (AppWindows.AccountStatsWindow.ShouldOpenAtStartup) AppWindows.AccountStatsWindow.Show();
                if (AppWindows.DinosaursWindow.ShouldOpenAtStartup) AppWindows.DinosaursWindow.Show();
                if (AppWindows.CurrentActivityWindow.ShouldOpenAtStartup) AppWindows.CurrentActivityWindow.Show();
                _activationReady.TrySetResult();
                if (_initialActivation is { } activation
                    && (activation.Kind != Microsoft.Windows.AppLifecycle.ExtendedActivationKind.Launch || Environment.GetCommandLineArgs().Length > 1))
                    await ProcessActivationAsync(activation, initial: true);
            }
            catch (Exception ex)
            {
                LogException("OnLaunched", ex);
                throw;
            }
#endif
        }

        private bool _shuttingDown;
        private bool _quitRequested;

        private MainWindow CreateMainWindow(bool activate = true, MainWindowState? state = null)
        {
            if (_shuttingDown || _quitRequested) throw new InvalidOperationException("The application is stopping.");
            var window = new MainWindow(state);
            _mainWindows.Add(window);
            _window = window;
            var appWindow = window.AppWindow;
            global::Windows.Foundation.TypedEventHandler<Microsoft.UI.Windowing.AppWindow, Microsoft.UI.Windowing.AppWindowClosingEventArgs>
                closing = (_, args) => MainWindow_Closing(window, args);
            appWindow.Closing += closing;
            void Activated(object sender, WindowActivatedEventArgs args)
            {
                if (args.WindowActivationState != WindowActivationState.Deactivated && !_closingMainWindows.Contains(window))
                    _window = window;
            }
            void Closed(object sender, WindowEventArgs args)
            {
                window.Activated -= Activated;
                window.Closed -= Closed;
                appWindow.Closing -= closing;
                MainWindow_Closed(window, args);
            }
            window.Closed += Closed;
            window.Activated += Activated;
            if (!_startupDiagnosticsScheduled && !_runtimeFirstChanceDiagnostics)
            {
                _startupDiagnosticsScheduled = true;
                _ = window.ContentInitialization.ContinueWith(
                    _ => AppDomain.CurrentDomain.FirstChanceException -= CurrentDomain_FirstChanceException,
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            if (activate) window.Activate();
            return window;
        }

        internal const string MainWindowStatesKey = "mainWindowStates";

        internal static MainWindow[] OpenSavedMainWindows()
        {
            MainWindowState[] states;
            try
            {
                var data = Core.AppDefaults.Shared.GetValue<JsonElement?>(MainWindowStatesKey);
                states = data?.Deserialize<MainWindowState[]>() ?? [];
                foreach (var state in states)
                {
                    if (state is null || state.Version != 1 || state.Bounds is null
                        || state.Bounds.Width <= 0 || state.Bounds.Height <= 0
                        || !double.IsFinite(state.SidebarWidth) || !double.IsFinite(state.TimelineWidth)
                        || !double.IsFinite(state.ColumnLayoutTimelineHeight) || state.ColumnLayoutTimelineHeight < 0
                        || state.Timeline?.SortParameters is { } sort && !Enum.IsDefined(sort.Key)
                        || state.Detail is { } detail && (!double.IsFinite(detail.ScrollY) || detail.ScrollY < 0))
                        throw new InvalidDataException("The saved window layout is invalid or uses an unsupported version.");
                }
            }
            catch (Exception error) when (error is JsonException or InvalidDataException)
            {
                var window = OpenMainWindow();
                window.ReportStateError("Read saved windows", error);
                return [window];
            }
            return states.Length == 0 ? [OpenMainWindow()] : states.Select(state => OpenMainWindow(state)).ToArray();
        }

        internal static bool TrySaveMainWindows(IEnumerable<MainWindow> windows)
        {
            var snapshot = windows.OrderBy(window => ReferenceEquals(window, MainWindow) ? 1 : 0).ToArray();
            if (snapshot.Length == 0) return true;
            try
            {
                Core.AppDefaults.Shared.SetValue(MainWindowStatesKey, snapshot.Select(window => window.CaptureState()).ToArray());
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                foreach (var window in MainWindows) window.ReportStateError("Save windows", error);
                return false;
            }
        }

        internal static async Task<bool> RefreshWindowScrollPositionsAsync(IEnumerable<MainWindow> windows)
        {
            var snapshot = windows.ToArray();
            try
            {
                await Task.WhenAll(snapshot.Select(window => window.RefreshScrollPositionAsync()));
                return true;
            }
            catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException
                or JsonException or InvalidDataException or TimeoutException)
            {
                foreach (var window in snapshot) window.ReportStateError("Capture reading position", error);
                return false;
            }
        }

        private async void MainWindow_Closing(MainWindow window, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
        {
            if (_allowNativeClose.Contains(window)) return;
            args.Cancel = true;
            await CloseMainWindowCoreAsync(window);
        }

        private async Task<bool> CloseMainWindowCoreAsync(MainWindow window)
        {
            await _windowCloseGate.WaitAsync();
            try
            {
                if (!_mainWindows.Contains(window)) return true;
                if (!_quitRequested)
                {
                    if (!await RefreshWindowScrollPositionsAsync(MainWindows)) return false;
                    var remaining = MainWindows.Where(other => !ReferenceEquals(other, window)).ToArray();
                    if (!TrySaveMainWindows(remaining.Length == 0 ? [window] : remaining)) return false;
                }
                _closingMainWindows.Add(window);
                bool lastWindow = !_mainWindows.Any(other => !_closingMainWindows.Contains(other));
                if (lastWindow) _shuttingDown = true;
                if (window.Content is UIElement content) content.IsHitTestVisible = false;
                await window.ShutdownContentAsync();
                if (lastWindow)
                {
                    try { await WinNewsWire.AppWindows.WindowThemeHelper.CloseAllSecondaryWindowsAsync(); }
                    catch (Exception ex) { LogException("CloseAllSecondaryWindows", ex); }
                    try { await WinNewsWire.AppRuntime.AppService.Shared.StopAsync(); }
                    catch (Exception ex) { LogException("AppService.Stop", ex); }
                }
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnClosed(object sender, WindowEventArgs args) => closed.TrySetResult();
                window.Closed += OnClosed;
                _allowNativeClose.Add(window);
                // Window.Close destroys the window; it does not raise AppWindow.Closing.
                if (!window.DispatcherQueue.TryEnqueue(window.CloseAfterShutdown))
                {
                    LogException("MainWindow.Closing", null, "The UI dispatcher could not finish closing the window.");
                    Exit();
                    return false;
                }
                try { await closed.Task; }
                finally { window.Closed -= OnClosed; }
                return true;
            }
            finally { _windowCloseGate.Release(); }
        }

        private void MainWindow_Closed(MainWindow window, Microsoft.UI.Xaml.WindowEventArgs args)
        {
            _mainWindows.Remove(window);
            _closingMainWindows.Remove(window);
            _allowNativeClose.Remove(window);
            if (ReferenceEquals(_window, window)) _window = MainWindows.LastOrDefault();
            if (_mainWindows.Count > 0) return;
            if (_activationInstance is not null) _activationInstance.Activated -= Instance_Activated;
            if (_notificationActivation is not null) _notificationActivation.NotificationInvoked -= Notification_Invoked;
            try { Exit(); }
            catch (Exception ex) { LogException("Application.Exit", ex); }
        }
    }
}
