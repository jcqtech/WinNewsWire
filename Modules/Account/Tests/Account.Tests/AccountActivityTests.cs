using WinNewsWire.ActivityLog;
using Xunit;
using ActivityLogger = WinNewsWire.ActivityLog.ActivityLog;

namespace WinNewsWire.Account.Tests;

public sealed class AccountActivityTests : IDisposable
{
    private readonly TestAccountManager _manager = new();

    [Fact]
    public async Task MutationsRecordTheirLifecycleAndOwner()
    {
        var account = _manager.CreateAccount();
        var folder = await account.CreateFolderAsync("Original");
        await account.RenameFolderAsync(folder, "Renamed");
        await account.RemoveFolderAsync(folder);
        var activities = ActivityLogger.Shared.CompletedActivitiesFor(account.ActivityOwner);
        Assert.Equal([ActivityKind.CreateFolder, ActivityKind.RenameFolder, ActivityKind.RemoveFolder], activities.Select(activity => activity.Kind));
        Assert.All(activities, activity =>
        {
            Assert.Equal(ActivityState.Completed, activity.State);
            Assert.NotNull(activity.StartDate);
            Assert.NotNull(activity.EndDate);
        });
    }

    [Fact]
    public async Task FailedMutationIsLoggedAndStillThrows()
    {
        var account = _manager.CreateAccount();
        var folder = await account.CreateFolderAsync("Folder");
        await Assert.ThrowsAsync<ArgumentException>(() => account.RenameFolderAsync(folder, " "));
        var activity = ActivityLogger.Shared.CompletedActivitiesFor(account.ActivityOwner).Last();
        Assert.Equal(ActivityKind.RenameFolder, activity.Kind);
        Assert.Equal(ActivityState.Failed, activity.State);
        Assert.IsType<ArgumentException>(activity.Error);
        Assert.Equal("Folder", folder.Name);
    }

    public void Dispose() => _manager.Dispose();
}
