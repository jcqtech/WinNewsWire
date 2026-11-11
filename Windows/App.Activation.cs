using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.DataTransfer.ShareTarget;
using Windows.Storage;
using WinNewsWire.Core;

namespace WinNewsWire;

public partial class App
{
    internal static void OpenScriptingLocation(AppActivationRequest request)
    {
        if (Current is not App app || IsShuttingDown
            || app._activationDispatcher?.TryEnqueue(async () =>
                await app.ProcessActivationWorkAsync(() => [new(request)])) != true)
            throw new InvalidOperationException("The application cannot open a scripting location while closing.");
    }

    private AppInstance? _activationInstance;
    private AppActivationArguments? _initialActivation;
    private (ShareOperation Operation, string Url)? _initialSharedFeed;
    private AppNotificationManager? _notificationActivation;
    private DispatcherQueue? _activationDispatcher;
    private readonly TaskCompletionSource _activationReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _activationGate = new(1, 1);
    private sealed record ActivationWork(AppActivationRequest Request, StorageFile? File = null);

    private async Task<bool> RegisterActivationAsync()
    {
        _activationDispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("Activation requires the application dispatcher.");
        try
        {
            _activationInstance = AppInstance.FindOrRegisterForKey("WinNewsWire.Main");
            _initialActivation = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (!_activationInstance.IsCurrent)
            {
                await _activationInstance.RedirectActivationToAsync(_initialActivation);
                Exit();
                return false;
            }
            _activationInstance.Activated += Instance_Activated;
            if (_initialActivation.Kind == ExtendedActivationKind.ShareTarget
                && _initialActivation.Data is ShareTargetActivatedEventArgs share)
            {
                var operation = share.ShareOperation;
                operation.ReportStarted();
                var url = await Services.SharedFeedDataReader.ReadAsync(operation.Data);
                operation.ReportDataRetrieved();
                _initialSharedFeed = (operation, url);
            }
            try
            {
                _notificationActivation = AppNotificationManager.Default;
                _notificationActivation.NotificationInvoked += Notification_Invoked;
            }
            catch (Exception error) when (error is COMException or InvalidOperationException)
            {
                LogException("Observe notification activation", error);
            }
            return true;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException
            or IOException or UnauthorizedAccessException or NotSupportedException or OperationCanceledException)
        {
            LogException("Register application activation", error);
            if (_initialActivation?.Data is ShareTargetActivatedEventArgs share)
            {
                try { share.ShareOperation.ReportError(error.Message); }
                catch (Exception reportError) when (reportError is COMException or InvalidOperationException)
                {
                    LogException("Report share activation failure", reportError);
                }
            }
            MessageBoxW(IntPtr.Zero, "WinNewsWire could not contact or register its running instance.\n\n" + error.Message,
                "WinNewsWire could not start", 0x10);
            Environment.ExitCode = 1;
            Exit();
            return false;
        }
    }

    private void Instance_Activated(object? sender, AppActivationArguments args)
    {
        if (_activationDispatcher?.TryEnqueue(async () => await ProcessActivationAsync(args, initial: false)) != true)
            LogException("Dispatch activation", null, "The application dispatcher is unavailable.");
    }

    private void Notification_Invoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        if (_activationDispatcher?.TryEnqueue(async () => await ProcessActivationWorkAsync(() =>
            [new(AppActivationRequest.FromNotification(args.Arguments.ToDictionary(pair => pair.Key, pair => pair.Value)))])) != true)
            LogException("Dispatch notification activation", null, "The application dispatcher is unavailable.");
    }

    private Task ProcessActivationAsync(AppActivationArguments args, bool initial)
        => args.Kind == ExtendedActivationKind.ShareTarget && args.Data is ShareTargetActivatedEventArgs share
            ? initial && _initialSharedFeed is { } received
                ? ProcessShareActivationAsync(received.Operation, received.Url)
                : ProcessShareActivationAsync(share.ShareOperation)
            : ProcessActivationWorkAsync(() => ActivationRequests(args, initial));

    private async Task ProcessShareActivationAsync(ShareOperation operation, string? receivedUrl = null)
    {
        bool ownsGate = false;
        try
        {
            string url;
            if (receivedUrl is not null) url = receivedUrl;
            else
            {
                operation.ReportStarted();
                url = await Services.SharedFeedDataReader.ReadAsync(operation.Data);
                operation.ReportDataRetrieved();
            }
            await _activationReady.Task;
            await _activationGate.WaitAsync();
            ownsGate = true;
            if (IsShuttingDown) throw new InvalidOperationException("WinNewsWire is closing. Share the link again after it exits.");
            var window = MainWindow as MainWindow ?? MainWindows.LastOrDefault() ?? OpenMainWindow();
            if (await window.HandleSharedFeedAsync(url)) operation.ReportCompleted();
            else operation.ReportError(AppShared.AppStrings.Get("ShareSubscriptionCancelled"));
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException
            or InvalidOperationException or COMException or NotSupportedException or FormatException
            or System.Net.Http.HttpRequestException or System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException
            or OperationCanceledException or FeedFinder.FeedNotFoundException)
        {
            LogException("Receive shared feed", error);
            try { operation.ReportError(error.Message); }
            catch (Exception reportError) when (reportError is COMException or InvalidOperationException)
            {
                LogException("Report shared-feed error", reportError);
            }
            if (!IsShuttingDown && MainWindows.LastOrDefault() is { } window)
                window.ReportStateError("Receive shared feed", error);
        }
        finally
        {
            if (ownsGate) _activationGate.Release();
            if (receivedUrl is not null) _initialSharedFeed = null;
        }
    }

    private async Task ProcessActivationWorkAsync(Func<IEnumerable<ActivationWork>> requests)
    {
        await _activationReady.Task;
        await _activationGate.WaitAsync();
        try
        {
            if (IsShuttingDown) throw new InvalidOperationException("WinNewsWire is closing. Open this item again after it exits.");
            foreach (var work in requests())
            {
                var window = MainWindow as MainWindow ?? MainWindows.LastOrDefault() ?? OpenMainWindow();
                await window.HandleActivationAsync(work.Request, work.File);
            }
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException
            or InvalidOperationException or COMException or NotSupportedException or FormatException
            or System.Net.Http.HttpRequestException or System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException
            or OperationCanceledException)
        {
            LogException("Open activation", error);
            IntPtr owner = IntPtr.Zero;
            if (MainWindows.LastOrDefault() is { } window && !IsShuttingDown)
            {
                owner = WinRT.Interop.WindowNative.GetWindowHandle(window);
                window.ReportStateError("Open item", error);
            }
            MessageBoxW(owner, error.Message, "WinNewsWire could not open this item", 0x10);
        }
        finally { _activationGate.Release(); }
    }

    private static IEnumerable<ActivationWork> ActivationRequests(AppActivationArguments args, bool initial)
    {
        switch (args.Kind)
        {
            case ExtendedActivationKind.Protocol when args.Data is IProtocolActivatedEventArgs protocol:
                yield return new(AppActivationRequest.FromUri(protocol.Uri));
                break;
            case ExtendedActivationKind.File when args.Data is IFileActivatedEventArgs files:
                foreach (var item in files.Files)
                {
                    if (item is not StorageFile file) throw new ArgumentException("Only subscription and theme files can be opened.");
                    yield return new(new(AppActivationRequest.FileKind(file.Name), file.Path), file);
                }
                break;
            case ExtendedActivationKind.AppNotification when args.Data is AppNotificationActivatedEventArgs notification:
                yield return new(AppActivationRequest.FromNotification(notification.Arguments.ToDictionary(pair => pair.Key, pair => pair.Value)));
                break;
            case ExtendedActivationKind.Launch:
                string[] arguments;
                if (initial) arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
                else if (args.Data is ILaunchActivatedEventArgs launch)
                {
                    arguments = WindowsCommandLine.Split(launch.Arguments);
                    if (arguments.Length > 0 && string.Equals(Path.GetFileName(arguments[0]), Path.GetFileName(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase))
                        arguments = arguments.Skip(1).ToArray();
                }
                else throw new ArgumentException("Windows did not provide launch arguments.");
                if (arguments.Any(argument => argument.StartsWith("----AppNotificationActivated:", StringComparison.Ordinal)))
                    yield break;
                foreach (var request in AppActivationRequest.FromArguments(arguments, initial ? Environment.CurrentDirectory : null)) yield return new(request);
                break;
            default:
                throw new NotSupportedException("This Windows activation type is not supported: " + args.Kind);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
