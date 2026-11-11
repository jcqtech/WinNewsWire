using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinNewsWire.Core;
using WinNewsWire.Secrets;
using Xunit;

namespace WinNewsWire.Account.Tests;

[SupportedOSPlatform("windows")]
public sealed class CredentialsStorageTests : IDisposable
{
    private readonly TestAccountManager _storage = new();

    [Fact]
    public void UsernameAndServerPunctuationCannotCollide()
    {
        var first = new Credentials(CredentialsType.Basic, "user+name", "first-test-secret");
        var second = first with { Username = "user_name", Secret = "second-test-secret" };
        CredentialsManager.Store(first, "server+one");
        CredentialsManager.Store(second, "server_one");
        Assert.Equal(first, CredentialsManager.Retrieve(first.Type, "server+one", first.Username));
        Assert.Equal(second, CredentialsManager.Retrieve(second.Type, "server_one", second.Username));
        Assert.Equal(2, Directory.EnumerateFiles(AppConfig.EnsureSubdirectory("Credentials"), "*.dat").Count());
    }

    [Fact]
    public void OAuthPairsAreStoredInOneProtectedAtomicBundle()
    {
        var access = new Credentials(CredentialsType.OAuthAccessToken, "user", "test-access");
        var refresh = new Credentials(CredentialsType.OAuthRefreshToken, "user", "test-refresh");
        CredentialsManager.StoreMany([access, refresh], "Feedly");
        var path = Assert.Single(Directory.EnumerateFiles(AppConfig.EnsureSubdirectory("Credentials"), "*.dat"));
        Assert.DoesNotContain("test-access", Encoding.UTF8.GetString(File.ReadAllBytes(path)));
        Assert.Equal(access, CredentialsManager.Retrieve(access.Type, "Feedly", "user"));
        Assert.Equal(refresh, CredentialsManager.Retrieve(refresh.Type, "Feedly", "user"));
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => CredentialsManager.StoreMany(
                [access with { Secret = "new-access" }, refresh with { Secret = "new-refresh" }], "Feedly"));
        Assert.Equal(access, CredentialsManager.Retrieve(access.Type, "Feedly", "user"));
        Assert.Equal(refresh, CredentialsManager.Retrieve(refresh.Type, "Feedly", "user"));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public void ExistingDpapiFilesMigrateWithoutLosingOtherCredentialTypes()
    {
        var access = new Credentials(CredentialsType.OAuthAccessToken, "user", "old-access");
        var refresh = new Credentials(CredentialsType.OAuthRefreshToken, "user", "old-refresh");
        var accessPath = WriteLegacy(access, "Feedly");
        var refreshPath = WriteLegacy(refresh, "Feedly");
        Assert.Equal(access, CredentialsManager.Retrieve(access.Type, "Feedly", "user"));
        var updated = access with { Secret = "updated-access" };
        CredentialsManager.Store(updated, "Feedly");
        Assert.Equal(updated, CredentialsManager.Retrieve(access.Type, "Feedly", "user"));
        Assert.Equal(refresh, CredentialsManager.Retrieve(refresh.Type, "Feedly", "user"));
        Assert.False(File.Exists(accessPath));
        Assert.False(File.Exists(refreshPath));
    }

    [Fact]
    public void DeletionDoesNotFallBackToAStaleLegacyCopy()
    {
        var credentials = new Credentials(CredentialsType.Basic, "user", "test");
        CredentialsManager.Store(credentials, "Feedbin");
        CredentialsManager.Remove(credentials.Type, "Feedbin", credentials.Username);
        WriteLegacy(credentials, "Feedbin");
        Assert.Null(CredentialsManager.Retrieve(credentials.Type, "Feedbin", credentials.Username));
    }

    [Fact]
    public void ACollidingLegacyFilenameNeverReturnsAnotherUsersCredentials()
    {
        var credentials = new Credentials(CredentialsType.Basic, "user+name", "test");
        WriteLegacy(credentials, "Feedbin");
        Assert.Null(CredentialsManager.Retrieve(credentials.Type, "Feedbin", "user_name"));
    }

    private static string WriteLegacy(Credentials credentials, string server)
    {
        static string Safe(string value) => new(value.Select(character => char.IsLetterOrDigit(character) || character is '.' or '-' ? character : '_').ToArray());
        var path = Path.Combine(AppConfig.EnsureSubdirectory("Credentials"), $"{Safe(server)}_{credentials.Type}_{Safe(credentials.Username)}.dat");
        File.WriteAllBytes(path, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(credentials), null, DataProtectionScope.CurrentUser));
        return path;
    }
    public void Dispose() => _storage.Dispose();
}
