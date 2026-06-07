using System;
using System.IO;
using System.Threading.Tasks;
using WinNewsWire.Account;
using WinNewsWire.AppShared.SmartFeeds;
using WinNewsWire.AppShared.Timer;
using WinNewsWire.Core;
using WinNewsWire.ErrorLog;
using WinNewsWire.Secrets;

namespace WinNewsWire.AppRuntime;

/// <summary>Top-level app runtime. Wires AccountManager, SmartFeedsController, refresh timer,
/// error log, undo manager, app defaults. One shared instance per process.</summary>
public sealed class AppService
{
    public static AppService Shared { get; } = new();

    public AccountManager Accounts => AccountManager.Shared;
    public SmartFeedsController SmartFeeds => SmartFeedsController.Shared;
    public AppDefaults Defaults => AppDefaults.Shared;
    public UndoManager Undo { get; } = new();
    public ErrorLogDatabase ErrorLog { get; }

    private readonly AccountRefreshTimer _refreshTimer;
    private readonly ArticleStatusSyncTimer _syncTimer;
    private readonly WinNewsWire.Services.NewArticleNotifier _notifier;

    static AppService()
    {
        // Registry mapping AccountType -> IAccountDelegate. Remote-account modules live
        // outside the Account module and are composed in here at the app layer.
        AccountDelegateFactory.Resolver = (type, accountID) =>
        {
            // Credentials are looked up from DPAPI-backed store by account ID (we persist
            // the username as the account ID suffix for lookup). If none found, the delegate
            // will simply be inactive until the user enters credentials.
            Credentials? creds = null;
            try { creds = TryLoadCredentialsFor(type, accountID); } catch { }
            return type switch
            {
                AccountType.Feedbin => new Feedbin.FeedbinAccountDelegate(creds),
                AccountType.NewsBlur => new NewsBlur.NewsBlurAccountDelegate(creds),
                AccountType.FreshRSS => new ReaderAPI.ReaderAPIAccountDelegate(ReaderAPI.ReaderAPIVariant.FreshRSS, host: null, credentials: creds),
                AccountType.Inoreader => new ReaderAPI.ReaderAPIAccountDelegate(ReaderAPI.ReaderAPIVariant.Inoreader, host: null, credentials: creds),
                AccountType.BazQux => new ReaderAPI.ReaderAPIAccountDelegate(ReaderAPI.ReaderAPIVariant.BazQux, host: null, credentials: creds),
                AccountType.TheOldReader => new ReaderAPI.ReaderAPIAccountDelegate(ReaderAPI.ReaderAPIVariant.TheOldReader, host: null, credentials: creds),
                AccountType.Feedly => new Feedly.FeedlyAccountDelegate(
                    accessToken: creds,
                    refreshToken: CredentialsManager.Retrieve(CredentialsType.OAuthRefreshToken, "Feedly", accountID),
                    config: Feedly.FeedlyClientConfig.Load()),
                _ => new LocalAccountDelegate(),
            };
        };
    }

    private static Credentials? TryLoadCredentialsFor(AccountType type, string accountID)
    {
        var credType = type switch
        {
            AccountType.NewsBlur => CredentialsType.NewsBlurSessionId,
            AccountType.FreshRSS or AccountType.Inoreader or AccountType.BazQux or AccountType.TheOldReader => CredentialsType.ReaderBasic,
            AccountType.Feedly => CredentialsType.OAuthAccessToken,
            _ => CredentialsType.Basic,
        };
        return CredentialsManager.Retrieve(credType, type.ToString(), accountID);
    }

    private AppService()
    {
        ErrorLog = new ErrorLogDatabase(Path.Combine(AppConfig.LogsDirectory, "errors.sqlite3"));
        _refreshTimer = new AccountRefreshTimer(Accounts, (RefreshInterval)Defaults.RefreshIntervalRaw);
        _syncTimer = new ArticleStatusSyncTimer(Accounts, TimeSpan.FromMinutes(5));
        _notifier = new WinNewsWire.Services.NewArticleNotifier(Accounts);
        Defaults.Changed += OnDefaultsChanged;
    }

    public void Start()
    {
        _refreshTimer.Start();
        _syncTimer.Start();
        _notifier.Start();
        _ = Task.Run(async () =>
        {
            try { await Accounts.RefreshAllAsync(); } catch (Exception ex) { await LogAsync("Initial refresh", ex); }
            try { await SmartFeeds.RefreshAllAsync(); } catch (Exception ex) { await LogAsync("SmartFeeds refresh", ex); }
        });
    }

    public void Stop()
    {
        _refreshTimer.Stop();
        _syncTimer.Stop();
        _notifier.Stop();
    }

    private void OnDefaultsChanged(object? sender, string key)
    {
        if (key == AppDefaults.Key.RefreshInterval)
            _refreshTimer.Interval = (RefreshInterval)Defaults.RefreshIntervalRaw;
    }

    public Task LogAsync(string operation, Exception ex)
        => ErrorLog.AddAsync(new ErrorLogEntry(0, DateTime.UtcNow, "App", 0, operation, "", "", 0, ex.ToString()));
}
