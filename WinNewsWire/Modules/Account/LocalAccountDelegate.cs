using WinNewsWire.FeedFinder;
using WinNewsWire.Web;

namespace WinNewsWire.Account;

/// <summary>
/// Port of <c>LocalAccountDelegate</c> + <c>InitialFeedDownloader</c>.
/// Single-feed discovery uses <see cref="Downloader"/>; batch refresh delegates
/// to <see cref="LocalAccountRefresher"/> which drives a <see cref="DownloadSession"/>.
/// </summary>
public sealed class LocalAccountDelegate : IAccountDelegate
{
    public AccountType Type => AccountType.OnMyMac;

    public async Task<Feed?> CreateFeedAsync(Account account, string urlOrSite, string? name, Folder? folder, CancellationToken ct)
    {
        string feedUrl = urlOrSite;
        try
        {
            var specs = await FeedFinder.FeedFinder.FindAsync(urlOrSite, ct);
            var best = FeedSpecifier.BestFeed(specs);
            if (best is not null) feedUrl = best.UrlString;
            if (name is null && best?.Title is { Length: > 0 }) name = best.Title;
        }
        catch (FeedNotFoundException) { /* allow direct add */ }

        return account.AddFeed(feedUrl, name, folder);
    }

    public async Task RefreshAllAsync(Account account, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        using var refresher = new LocalAccountRefresher(account);
        await refresher.RefreshAllAsync(progress, ct);
    }
}
