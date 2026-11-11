using WinNewsWire.ActivityLog;
using ActivityLogger = WinNewsWire.ActivityLog.ActivityLog;

namespace WinNewsWire.Account;

public sealed partial class Account
{
    public ActivityOwner ActivityOwner => WinNewsWire.ActivityLog.ActivityOwner.Account(AccountID, NameForDisplay);

    public Task<T> LogActivityAsync<T>(ActivityKind kind, Func<Task<T>> work, string? detail = null,
        Func<T, string?>? successMessage = null, Func<T, bool>? durationIsSignificant = null)
        => ActivityLogger.Shared.LogActivityAsync(ActivityOwner, kind, work, detail, successMessage, durationIsSignificant);

    public Task LogActivityAsync(ActivityKind kind, Func<Task> work, string? detail = null, Func<string?>? successMessage = null)
        => ActivityLogger.Shared.LogActivityAsync(ActivityOwner, kind, work, detail, successMessage);
}
