using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinNewsWire.Core;

namespace WinNewsWire.Secrets;

[SupportedOSPlatform("windows")]
public static class CredentialsManager
{
    private static readonly object Gate = new();
    private static string Root => AppConfig.EnsureSubdirectory("Credentials");

    private static string BundlePath(string server, string username)
    {
        var key = JsonSerializer.SerializeToUtf8Bytes(new[] { server, username });
        return Path.Combine(Root, Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant() + ".dat");
    }

    private static string LegacyPath(CredentialsType type, string server, string username)
        => Path.Combine(Root, $"{SafeHost(server)}_{type}_{SafeHost(username)}.dat");

    private static string SafeHost(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var c in value) result.Append(char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_');
        return result.ToString();
    }

    public static bool ContainsScope(string server, string username)
    {
        lock (Gate)
            return File.Exists(BundlePath(server, username)) ||
                Enum.GetValues<CredentialsType>().Any(type => File.Exists(LegacyPath(type, server, username)));
    }

    public static void Store(Credentials credentials, string server) => StoreMany([credentials], server);

    public static void StoreMany(IReadOnlyCollection<Credentials> credentials, string server)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        if (credentials.Count == 0) throw new ArgumentException("No credentials were supplied.", nameof(credentials));
        var username = credentials.First().Username;
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        if (credentials.Any(value => value.Username != username || !Enum.IsDefined(value.Type) || value.Secret is null))
            throw new ArgumentException("A credential bundle must contain valid types for one username.", nameof(credentials));
        lock (Gate)
        {
            var existing = LoadForUpdate(server, username);
            foreach (var value in credentials) existing[value.Type] = value;
            SaveBundle(server, username, existing);
            RemoveMigratedLegacyFiles(server, username);
        }
    }

    public static Credentials? Retrieve(CredentialsType type, string server, string username)
    {
        lock (Gate)
        {
            try
            {
                var path = BundlePath(server, username);
                return File.Exists(path) ? ReadBundle(path, username).GetValueOrDefault(type) : ReadLegacy(type, server, username);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
            {
                Trace.TraceError($"Could not read protected account credentials: {error.Message}");
                return null;
            }
        }
    }

    public static void Remove(CredentialsType type, string server, string username)
    {
        lock (Gate)
        {
            var existing = LoadForUpdate(server, username);
            existing.Remove(type);
            // An empty bundle is authoritative, preventing deleted credentials from reappearing through legacy fallback.
            SaveBundle(server, username, existing);
            RemoveMigratedLegacyFiles(server, username);
        }
    }

    private static Dictionary<CredentialsType, Credentials> LoadForUpdate(string server, string username)
    {
        var path = BundlePath(server, username);
        if (File.Exists(path)) return ReadBundle(path, username);
        var values = new Dictionary<CredentialsType, Credentials>();
        foreach (var type in Enum.GetValues<CredentialsType>())
            if (ReadLegacy(type, server, username) is { } credentials) values[type] = credentials;
        return values;
    }

    private static Dictionary<CredentialsType, Credentials> ReadBundle(string path, string username)
    {
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        var values = JsonSerializer.Deserialize<Dictionary<CredentialsType, Credentials>>(bytes)
            ?? throw new InvalidDataException("The protected credential bundle is empty.");
        if (values.Any(pair => !Enum.IsDefined(pair.Key) || pair.Value is null || pair.Key != pair.Value.Type || pair.Value.Username != username || pair.Value.Secret is null))
            throw new InvalidDataException("The protected credential bundle has an invalid identity.");
        return values;
    }

    private static Credentials? ReadLegacy(CredentialsType type, string server, string username)
    {
        var path = LegacyPath(type, server, username);
        if (!File.Exists(path)) return null;
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        var value = JsonSerializer.Deserialize<Credentials>(bytes);
        if (value?.Type == type && value.Username == username && value.Secret is not null) return value;
        Trace.TraceWarning("Ignored a legacy credential filename whose stored identity does not match the requested identity.");
        return null;
    }

    private static void SaveBundle(string server, string username, Dictionary<CredentialsType, Credentials> values)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(values);
        var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        FileUtilities.WriteAllBytesAtomically(BundlePath(server, username), protectedBytes);
    }

    private static void RemoveMigratedLegacyFiles(string server, string username)
    {
        foreach (var type in Enum.GetValues<CredentialsType>())
        {
            try
            {
                if (ReadLegacy(type, server, username) is not null) File.Delete(LegacyPath(type, server, username));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
            {
                Trace.TraceWarning($"Could not remove a migrated credential file: {error.Message}");
            }
        }
    }
}
