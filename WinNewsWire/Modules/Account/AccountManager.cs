using System.Text.Json;
using WinNewsWire.Core;

namespace WinNewsWire.Account;

/// <summary>Maps <see cref="AccountType"/> to an <see cref="IAccountDelegate"/>. Remote-account
/// modules (Feedbin / NewsBlur / ReaderAPI / Feedly) register themselves through this factory
/// so the <see cref="AccountManager"/> can reload persisted accounts without depending on them.</summary>
public static class AccountDelegateFactory
{
    public static Func<AccountType, string, IAccountDelegate>? Resolver { get; set; }

    public static IAccountDelegate Resolve(AccountType type, string accountID)
    {
        if (Resolver is not null)
        {
            try { return Resolver(type, accountID); }
            catch { /* fall through */ }
        }
        return new LocalAccountDelegate();
    }
}

/// <summary>Port of <c>AccountManager</c>.</summary>
public sealed class AccountManager
{
    public static AccountManager Shared { get; } = new();

    private readonly Dictionary<string, Account> _accounts = new();
    public IReadOnlyCollection<Account> Accounts => _accounts.Values;
    public IEnumerable<Account> ActiveAccounts => _accounts.Values.Where(a => a.IsActive);
    public Account? DefaultAccount => _accounts.Values.FirstOrDefault();

    public event EventHandler? AccountsDidChange;

    private AccountManager()
    {
        LoadAccounts();
        if (_accounts.Count == 0) CreateLocalAccount();
    }

    private sealed record AccountMeta(int Type, string Name, bool IsActive);

    private static string MetaPath(string dir) => Path.Combine(dir, "Meta.json");

    private void LoadAccounts()
    {
        var root = AppConfig.AccountsDirectory;
        if (!Directory.Exists(root)) return;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var id = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(id)) continue;

            AccountType type = AccountType.OnMyMac;
            string name = "On My PC";
            bool active = true;
            try
            {
                if (File.Exists(MetaPath(dir)))
                {
                    var meta = JsonSerializer.Deserialize<AccountMeta>(File.ReadAllText(MetaPath(dir)));
                    if (meta is not null) { type = (AccountType)meta.Type; name = meta.Name; active = meta.IsActive; }
                }
                else if (id.StartsWith("Feedbin", StringComparison.Ordinal)) type = AccountType.Feedbin;
                else if (id.StartsWith("NewsBlur", StringComparison.Ordinal)) type = AccountType.NewsBlur;
                else if (id.StartsWith("FreshRSS", StringComparison.Ordinal)) type = AccountType.FreshRSS;
                else if (id.StartsWith("Inoreader", StringComparison.Ordinal)) type = AccountType.Inoreader;
                else if (id.StartsWith("BazQux", StringComparison.Ordinal)) type = AccountType.BazQux;
                else if (id.StartsWith("TheOldReader", StringComparison.Ordinal)) type = AccountType.TheOldReader;
                else if (id.StartsWith("Feedly", StringComparison.Ordinal)) type = AccountType.Feedly;
            }
            catch { }

            var del = type == AccountType.OnMyMac ? new LocalAccountDelegate() : AccountDelegateFactory.Resolve(type, id);
            var account = new Account(id, type, name, del) { IsActive = active };
            _accounts[id] = account;
        }
    }

    public Account CreateLocalAccount(string name = "On My PC")
        => CreateAccount(AccountType.OnMyMac, name, new LocalAccountDelegate(), "OnMyMac");

    public Account CreateRemoteAccount(AccountType type, string name, IAccountDelegate? @delegate = null)
    {
        var prefix = type.ToString();
        var id = $"{prefix}-{Guid.NewGuid():N}";
        @delegate ??= AccountDelegateFactory.Resolve(type, id);
        return CreateAccount(type, name, @delegate, id);
    }

    private Account CreateAccount(AccountType type, string name, IAccountDelegate @delegate, string preferredId)
    {
        var id = preferredId;
        if (_accounts.ContainsKey(id)) id = $"{type}-{Guid.NewGuid():N}";
        var a = new Account(id, type, name, @delegate);
        _accounts[id] = a;
        WriteMeta(a);
        AccountsDidChange?.Invoke(this, EventArgs.Empty);
        return a;
    }

    public void RemoveAccount(Account a)
    {
        if (!_accounts.Remove(a.AccountID)) return;
        try { a.Dispose(); } catch { }
        try { Directory.Delete(a.AccountDirectory, recursive: true); } catch { }
        AccountsDidChange?.Invoke(this, EventArgs.Empty);
    }

    public void SaveAccountMeta(Account a) => WriteMeta(a);

    private static void WriteMeta(Account a)
    {
        try
        {
            var meta = new AccountMeta((int)a.Type, a.Name, a.IsActive);
            File.WriteAllText(MetaPath(a.AccountDirectory),
                JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public async Task RefreshAllAsync(IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        foreach (var a in ActiveAccounts) await a.RefreshAllAsync(progress, ct);
    }
}
