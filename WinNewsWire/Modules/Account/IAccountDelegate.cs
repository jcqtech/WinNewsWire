namespace WinNewsWire.Account;

/// <summary>Port of <c>AccountDelegate</c>.</summary>
public interface IAccountDelegate
{
    AccountType Type { get; }
    /// <summary>When true, <see cref="Account.MarkAsync"/> also queues changes in the account's
    /// SyncDatabase so <see cref="SendArticleStatusAsync"/> can flush them to the remote service.</summary>
    bool SupportsRemoteSync => false;
    Task<Feed?> CreateFeedAsync(Account account, string urlOrSite, string? name, Folder? folder, CancellationToken ct);
    Task RefreshAllAsync(Account account, IProgress<ProgressInfo>? progress, CancellationToken ct);
    Task SendArticleStatusAsync(Account account, CancellationToken ct = default) => Task.CompletedTask;
    Task RefreshArticleStatusAsync(Account account, CancellationToken ct = default) => Task.CompletedTask;
}
