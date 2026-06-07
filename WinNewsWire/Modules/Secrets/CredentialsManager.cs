using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinNewsWire.Secrets;

/// <summary>
/// Port of <c>CredentialsManager</c> using Windows DPAPI-backed storage.
/// The keychain concept maps to DPAPI-protected per-user files. Tokens are
/// scoped by (server, type, username) just like the Mac implementation.
/// </summary>
[SupportedOSPlatform("windows")]
public static class CredentialsManager
{
    private static string Root
    {
        get
        {
            var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinNewsWire", "Credentials");
            Directory.CreateDirectory(p);
            return p;
        }
    }

    private static string PathFor(CredentialsType type, string server, string username)
    {
        var key = $"{SafeHost(server)}_{type}_{SafeHost(username)}.dat";
        return Path.Combine(Root, key);
    }

    private static string SafeHost(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(char.IsLetterOrDigit(c) || c == '.' || c == '-' ? c : '_');
        return sb.ToString();
    }

    public static void Store(Credentials credentials, string server)
    {
        var path = PathFor(credentials.Type, server, credentials.Username);
        var json = JsonSerializer.SerializeToUtf8Bytes(credentials);
        var enc = ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, enc);
    }

    public static Credentials? Retrieve(CredentialsType type, string server, string username)
    {
        var path = PathFor(type, server, username);
        if (!File.Exists(path)) return null;
        try
        {
            var enc = File.ReadAllBytes(path);
            var bytes = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Credentials>(bytes);
        }
        catch { return null; }
    }

    public static void Remove(CredentialsType type, string server, string username)
    {
        var path = PathFor(type, server, username);
        if (File.Exists(path)) File.Delete(path);
    }
}
