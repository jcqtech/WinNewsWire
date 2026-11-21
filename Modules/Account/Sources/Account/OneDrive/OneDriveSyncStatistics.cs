namespace WinNewsWire.Account.OneDrive;

public sealed record OneDriveSyncStatistics(int Folders, int Feeds, int StatusRecords, int ContentRecords,
    int Tombstones, long LocalReplicaBytes, DateTime? LastExchangeUtc, string? LastError,
    int StarredStatuses, int UnreadStatuses, int ReadStatuses, int StarredContent, int UnreadContent, int ReadContent,
    int EligibleContent)
{
    public override string ToString() => $"Subscriptions: {Folders:N0} folders, {Feeds:N0} feeds.\n"
        + $"Status records: {StatusRecords:N0} total; {StarredStatuses:N0} starred, {UnreadStatuses:N0} unstarred unread, {ReadStatuses:N0} unstarred read.\n"
        + $"Article content: {ContentRecords:N0} total; {StarredContent:N0} starred, {UnreadContent:N0} unstarred unread, {ReadContent:N0} unstarred read/orphaned.\n"
        + $"Eligible for content cleanup: {EligibleContent:N0}. Deletion markers retained: {Tombstones:N0}.\n"
        + $"Local sync metadata: {LocalReplicaBytes:N0} bytes. These are record counts, not OneDrive quota usage.";
}
