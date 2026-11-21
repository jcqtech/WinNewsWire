using WinNewsWire.Account.OneDrive;

namespace WinNewsWire.Account;

public sealed partial class Account
{
    public Task<OneDriveSyncStatistics> FetchOneDriveSyncStatisticsAsync(CancellationToken ct = default)
        => RunOperationAsync(_ => Task.FromResult(OneDriveDelegate().Statistics(this)), ct);

    public Task<OneDriveSyncStatistics> ScanOneDriveAsync(IProgress<OneDriveSyncProgress>? progress = null, CancellationToken ct = default)
        => RunOperationAsync(async token =>
        {
            await OneDriveDelegate().ScanAsync(this, progress, token);
            return OneDriveDelegate().Statistics(this);
        }, ct);

    public Task<OneDriveCleanupResult> CleanUpOneDriveContentAsync(CancellationToken ct = default,
        IProgress<OneDriveSyncProgress>? progress = null)
        => RunOperationAsync(token => OneDriveDelegate().CleanUpContentAsync(this, progress, token), ct);

    private OneDriveAccountDelegate OneDriveDelegate() => Delegate as OneDriveAccountDelegate
        ?? throw new InvalidOperationException("This is not a OneDrive account.");

    public Task ConfigureOneDriveAsync(string directory, bool? syncUnreadContent, CancellationToken ct = default)
        => RunOperationAsync(_ =>
        {
            if (Type != AccountType.OneDrive) throw new InvalidOperationException("This is not a OneDrive account.");
            var replacement = new OneDriveAccountDelegate(directory);
            Settings.EndpointUrl = new Uri(replacement.Server!);
            Delegate.Dispose();
            Delegate = replacement;
            if (syncUnreadContent is { } preference) replacement.SetUnreadContentPreference(this, preference);
            return Task.CompletedTask;
        }, ct);
}
