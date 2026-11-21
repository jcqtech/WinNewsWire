namespace WinNewsWire.Account.OneDrive;

public sealed record OneDriveSyncProgress(string Phase, int Completed, int Total);
public sealed record OneDriveCleanupResult(int ReadDeleted, int UnreadDeleted)
{
    public int TotalDeleted => ReadDeleted + UnreadDeleted;
    public override string ToString() => $"Removed {ReadDeleted:N0} read/orphaned and {UnreadDeleted:N0} unread article bodies.";
}
