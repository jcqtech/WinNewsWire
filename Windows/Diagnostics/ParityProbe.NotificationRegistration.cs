#if PARITY_PROBE
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WinNewsWire.Account;
using WinNewsWire.Services;

namespace WinNewsWire.Diagnostics;

internal static partial class ParityProbe
{
    private static async Task<object?> CheckNotificationRegistrationAsync(MainWindow window)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int registrations = 0, unregistrations = 0;
        bool registrationOnUi = true, unregistrationOnUi = true;
        await using var notifier = new NewArticleNotifier(AccountManager.Shared, () =>
        {
            registrationOnUi = window.DispatcherQueue.HasThreadAccess;
            Interlocked.Increment(ref registrations);
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The held notification registration was not released.");
        }, () =>
        {
            unregistrationOnUi = window.DispatcherQueue.HasThreadAccess;
            Interlocked.Increment(ref unregistrations);
        });
        try
        {
            var start = notifier.StartAsync();
            Require(ReferenceEquals(start, notifier.StartAsync()), "Repeated startup registered notifications twice.");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Require(!registrationOnUi && !start.IsCompleted, "Notification registration blocked the UI dispatcher.");
            var pulse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Require(window.DispatcherQueue.TryEnqueue(() => pulse.TrySetResult()), "The UI dispatcher rejected the responsiveness check.");
            await pulse.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var stop = notifier.StopAsync();
            Require(!stop.IsCompleted && ReferenceEquals(stop, notifier.StopAsync()), "Stopping did not drain pending registration exactly once.");
            release.Set();
            await Task.WhenAll(start, stop).WaitAsync(TimeSpan.FromSeconds(10));
            Require(registrations == 1 && unregistrations == 1 && !unregistrationOnUi,
                "Pending registration leaked or notification teardown ran on the UI thread.");
        }
        finally
        {
            release.Set();
        }
        int failedCleanup = 0;
        await using var failed = new NewArticleNotifier(AccountManager.Shared,
            () => throw new COMException("Expected notification registration failure."),
            () => Interlocked.Increment(ref failedCleanup));
        await failed.StartAsync();
        await failed.StopAsync();
        Require(failedCleanup == 0, "Failed notification registration attempted to unregister an unowned registration.");
        return new { RegistrationOffUi = true, UiRemainsResponsive = true, CloseDuringRegistration = true,
            ExactlyOnceCleanup = true, UnregistrationOffUi = true, ReportedFailure = true };
    }
}
#endif
