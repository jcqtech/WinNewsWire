using System.Text.Json;
using WinNewsWire.Core;
using System.Runtime.Versioning;
using WinNewsWire.Secrets;

namespace WinNewsWire.Account;

/// <summary>Port of <c>AccountManager</c>.</summary>
public sealed class AccountManager : IDisposable, IAsyncDisposable
{
    public const string NetNewsWireNewsUrl = "https://netnewswire.blog/feed.xml";
    private const string JsonNetNewsWireNewsUrl = "https://netnewswire.blog/feed.json";
    public const string DefaultAccountID = "OnMyMac";
    private static readonly Lazy<AccountManager> SharedManager = new(() => new AccountManager());
    public static AccountManager Shared => SharedManager.Value;

    private readonly Dictionary<string, Account> _accounts = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, TaskCompletionSource> _removing = new(StringComparer.Ordinal);
    private bool _stopping;
    public IReadOnlyCollection<Account> Accounts { get { lock (_gate) return _accounts.Values.ToArray(); } }
    public IEnumerable<Account> ActiveAccounts => Accounts.Where(a => a.IsActive && !a.IsStopping);
    public DateTime? LastRefreshCompletedDate => ActiveAccounts.Max(a => a.Settings.LastRefreshCompletedDate);
    public IEnumerable<Account> SortedAccounts => SortAccounts(Accounts);
    public IEnumerable<Account> SortedActiveAccounts => SortAccounts(ActiveAccounts);
    public Account DefaultAccount { get; }

    public event EventHandler? AccountsDidChange;
    public event EventHandler? ArticleStatusesQueued;

    internal AccountManager()
    {
        try
        {
            LoadAccounts();
            if (_accounts.TryGetValue(DefaultAccountID, out var account))
                DefaultAccount = account;
            else
            {
                if (Directory.Exists(Path.Combine(AppConfig.AccountsDirectory, DefaultAccountID)))
                    throw new IOException("The existing default On My PC account could not be loaded. Its profile was not replaced.");
                DefaultAccount = CreateLocalAccount();
            }
        }
        catch
        {
            foreach (var account in Accounts)
            {
                UnobserveAccount(account);
                DisposeAfterFailedCreation(account);
            }
            _accounts.Clear();
            throw;
        }
    }

    public static IEnumerable<Account> SortAccounts(IEnumerable<Account> accounts)
        => accounts.OrderBy(a => a.AccountID != DefaultAccountID)
            .ThenBy(a => a.NameForDisplay, LocalizedStandardComparer.Shared);

    public bool AnyAccountHasFeedWithUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        return ActiveAccounts.Any(account => account.ExistingFeedWithUrl(url) is not null);
    }

    public bool AnyAccountHasNetNewsWireNewsSubscription()
        => AnyAccountHasFeedWithUrl(NetNewsWireNewsUrl) || AnyAccountHasFeedWithUrl(JsonNetNewsWireNewsUrl);

    private sealed record AccountMeta(int Type, string Name, bool IsActive, string? Username = null, string? Server = null);

    private static string MetaPath(string dir) => Path.Combine(dir, "Meta.json");

    private void LoadAccounts()
    {
        var root = AppConfig.AccountsDirectory;
        if (!Directory.Exists(root)) return;
        foreach (var dir in Directory.EnumerateDirectories(root)
            .OrderBy(dir => Path.GetFileName(dir) != DefaultAccountID))
        {
            var id = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(id)) continue;

            AccountType type = AccountType.OnMyMac;
            string name = "On My PC";
            bool active = true;
            string? username = null, server = null;
            try
            {
                if (File.Exists(MetaPath(dir)))
                {
                    var meta = JsonSerializer.Deserialize<AccountMeta>(File.ReadAllText(MetaPath(dir)))
                        ?? throw new JsonException("Account metadata must contain an object.");
                    type = (AccountType)meta.Type;
                    name = meta.Name;
                    active = meta.IsActive;
                    username = meta.Username;
                    server = meta.Server;
                }
                else if (id.StartsWith("Feedbin", StringComparison.Ordinal)) type = AccountType.Feedbin;
                else if (id.StartsWith("NewsBlur", StringComparison.Ordinal)) type = AccountType.NewsBlur;
                else if (id.StartsWith("FreshRSS", StringComparison.Ordinal)) type = AccountType.FreshRSS;
                else if (id.StartsWith("Inoreader", StringComparison.Ordinal)) type = AccountType.Inoreader;
                else if (id.StartsWith("BazQux", StringComparison.Ordinal)) type = AccountType.BazQux;
                else if (id.StartsWith("TheOldReader", StringComparison.Ordinal)) type = AccountType.TheOldReader;
                else if (id.StartsWith("Feedly", StringComparison.Ordinal)) type = AccountType.Feedly;
            }
            catch (Exception ex) when (id != DefaultAccountID && ex is (IOException or UnauthorizedAccessException or JsonException))
            {
                System.Diagnostics.Trace.TraceError($"Could not load account metadata {id}: {ex}");
                continue;
            }

            try
            {
                if (id == DefaultAccountID && type != AccountType.OnMyMac)
                    throw new InvalidDataException("The default On My PC account must be a local account. Its existing profile was not replaced.");
                var settings = new AccountSettings(id, dir);
                settings.ImportWindowsMetadata(name, active, username, server);
                RegisterAccount(CreateAccountInstance(id, type, name, settings: settings));
            }
            catch (NotSupportedException ex) when (id != DefaultAccountID)
            {
                System.Diagnostics.Trace.TraceError($"Account {id} is not supported on this platform: {ex.Message}");
            }
            catch (Exception ex) when (id != DefaultAccountID && ex is (IOException or UnauthorizedAccessException or JsonException or Microsoft.Data.Sqlite.SqliteException))
            {
                System.Diagnostics.Trace.TraceError($"Could not load account {id}: {ex}");
            }
        }
    }

    public Account CreateLocalAccount(string name = "On My PC")
        => CreateAccount(AccountType.OnMyMac, name, new LocalAccountDelegate(), DefaultAccountID);

    public Account CreateRemoteAccount(AccountType type, string name, IAccountDelegate? @delegate = null)
    {
        var prefix = type.ToString();
        var id = $"{prefix}-{Guid.NewGuid():N}";
        return CreateAccount(type, name, @delegate, id);
    }

    [SupportedOSPlatform("windows")]
    public Account CreateAuthenticatedAccount(AccountType type, string name, IAccountDelegate accountDelegate, IReadOnlyCollection<Credentials> credentials)
    {
        Account.ValidateAuthentication(type, accountDelegate, credentials);
        CredentialsManager.StoreMany(credentials, AccountCredentials.ServerFor(type, accountDelegate.Server));
        return CreateRemoteAccount(type, name, accountDelegate);
    }

    private static Account CreateAccountInstance(string id, AccountType type, string name,
        IAccountDelegate? accountDelegate = null, AccountSettings? settings = null)
    {
        bool ownsDelegate = accountDelegate is null;
        accountDelegate ??= AccountDelegateFactory.Resolve(type, id, settings?.Username, settings?.EndpointUrl?.ToString());
        try { return new Account(id, type, name, accountDelegate, settings); }
        catch
        {
            if (ownsDelegate) DisposeAfterFailedCreation(accountDelegate);
            throw;
        }
    }

    private static void DisposeAfterFailedCreation(IDisposable resource)
    {
        try { resource.Dispose(); }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError($"Could not release resources after account creation failed: {error}");
        }
    }

    private Account CreateAccount(AccountType type, string name, IAccountDelegate? @delegate, string preferredId)
    {
        Account account;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            var id = preferredId;
            if (_accounts.ContainsKey(id) || Directory.Exists(Path.Combine(AppConfig.AccountsDirectory, id)))
                id = $"{type}-{Guid.NewGuid():N}";
            account = CreateAccountInstance(id, type, name, @delegate);
            try { WriteMeta(account); }
            catch
            {
                DisposeAfterFailedCreation(account);
                throw;
            }
            RegisterAccount(account);
        }
        AccountsDidChange?.Invoke(this, EventArgs.Empty);
        return account;
    }

    public async Task RemoveAccountAsync(Account account)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (!_accounts.TryGetValue(account.AccountID, out var existing) || !ReferenceEquals(existing, account))
                throw new InvalidOperationException("This account is no longer registered.");
            if (ReferenceEquals(account, DefaultAccount))
                throw new InvalidOperationException("The default On My PC account cannot be removed. It can be deactivated in its account settings.");
            if (!_removing.TryAdd(account.AccountID, completion))
                throw new InvalidOperationException("This account is already being removed.");
        }
        try
        {
            var deletedRoot = Path.Combine(AppConfig.DataDirectory, "DeletedAccounts");
            Directory.CreateDirectory(deletedRoot);
            var archivedDirectory = Path.Combine(deletedRoot, account.AccountID + "-" + Guid.NewGuid().ToString("N"));
            await account.DisposeAsync();
            try { Directory.Move(account.AccountDirectory, archivedDirectory); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                try
                {
                    var replacement = CreateAccountInstance(account.AccountID, account.Type, account.Name, settings: account.Settings);
                    RegisterAccount(replacement);
                    AccountsDidChange?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception reopenError)
                {
                    throw new AggregateException("The account could not be removed or reopened.", error, reopenError);
                }
                throw;
            }
            lock (_gate)
            {
                UnobserveAccount(account);
                _accounts.Remove(account.AccountID);
            }
            AccountsDidChange?.Invoke(this, EventArgs.Empty);
            try { Directory.Delete(archivedDirectory, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"The account was removed, but its archived files could not be deleted from {archivedDirectory}.", error);
            }
        }
        finally
        {
            lock (_gate) _removing.Remove(account.AccountID);
            completion.TrySetResult();
        }
    }

    public void SaveAccountMeta(Account account)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (!_accounts.TryGetValue(account.AccountID, out var existing) || !ReferenceEquals(existing, account) || account.IsStopping)
                throw new InvalidOperationException("This account is no longer available.");
            WriteMeta(account);
        }
    }

    private void RegisterAccount(Account account)
    {
        lock (_gate)
        {
            if (_accounts.TryGetValue(account.AccountID, out var previous))
                UnobserveAccount(previous);
            _accounts[account.AccountID] = account;
            account.ArticleStatusesQueued += AccountStatusesQueued;
            account.AccountStateChanged += AccountMetadataChanged;
            account.DisplayNameChanged += AccountMetadataChanged;
        }
    }

    private void UnobserveAccount(Account account)
    {
        account.ArticleStatusesQueued -= AccountStatusesQueued;
        account.AccountStateChanged -= AccountMetadataChanged;
        account.DisplayNameChanged -= AccountMetadataChanged;
    }

    private void AccountMetadataChanged(object? sender, EventArgs args) => AccountsDidChange?.Invoke(this, EventArgs.Empty);
    private void AccountStatusesQueued(object? sender, EventArgs args) => ArticleStatusesQueued?.Invoke(sender, args);

    internal static void WriteMeta(Account a)
    {
        a.CredentialUsername ??= a.Delegate.CredentialUsername;
        a.Server = a.Delegate.Server ?? a.Server;
        var meta = new AccountMeta((int)a.Type, a.Name, a.IsActive, a.CredentialUsername, a.Server);
        FileUtilities.WriteAllTextAtomically(MetaPath(a.AccountDirectory),
            JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
    }

    public async Task RefreshAllAsync(IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var all = Task.WhenAll(ActiveAccounts.Select(async account =>
        {
            try { await account.RefreshAllAsync(progress, ct); }
            catch (OperationCanceledException) when (account.IsStopping && !ct.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (account.IsStopping) { }
        }));
        try { await all; }
        catch (Exception) when (all.IsFaulted) { throw all.Exception!; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_removing.Count > 0) throw new InvalidOperationException("An account is being removed. Await DisposeAsync before closing the account manager.");
            _stopping = true;
        }
        foreach (var account in Accounts)
        {
            UnobserveAccount(account);
            account.Dispose();
        }
        lock (_gate) _accounts.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        Task[] removals;
        lock (_gate)
        {
            _stopping = true;
            removals = _removing.Values.Select(completion => completion.Task).ToArray();
        }
        await Task.WhenAll(removals).ConfigureAwait(false);
        await Task.WhenAll(Accounts.Select(account =>
        {
            UnobserveAccount(account);
            return account.DisposeAsync().AsTask();
        })).ConfigureAwait(false);
        lock (_gate) _accounts.Clear();
    }

    public async Task<bool> SyncArticleStatusAllAsync(CancellationToken ct = default)
    {
        var work = await Task.WhenAll(ActiveAccounts.Select(async account =>
        {
            try { return await account.SyncArticleStatusAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) when (account.IsStopping) { return false; }
            catch (ObjectDisposedException) when (account.IsStopping) { return false; }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceError($"Status synchronization failed for {account.AccountID}: {error}");
                return false;
            }
        }));
        return work.Any(changed => changed);
    }

    public async Task VacuumAccountDatabasesAsync(CancellationToken ct = default)
    {
        foreach (var account in Accounts)
        {
            ct.ThrowIfCancellationRequested();
            await account.VacuumDatabasesAsync(ct);
        }
    }
}
