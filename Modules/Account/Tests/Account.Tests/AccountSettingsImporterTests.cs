using Xunit;

namespace WinNewsWire.Account.Tests;

public sealed class AccountSettingsImporterTests : IDisposable
{
    private readonly SettingsTestDirectory _directory = new();
    private static readonly DateTime ReferenceDate = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private AccountSettingsImporter.ImportedSettings? Read() => AccountSettingsImporter.ReadSettingsFromPlist("testAccount123", _directory.Path);
    private void Write(Dictionary<string, object> values) => _directory.WritePlist("Settings.plist", values);

    [Fact]
    public void NoPlistFile() => Assert.Null(Read());

    [Fact]
    public void UnreadablePlistFile()
    {
        Directory.CreateDirectory(Path.Combine(_directory.Path, "Settings.plist"));
        Assert.Null(Read());
    }

    [Fact]
    public void CorruptedPlistFile()
    {
        File.WriteAllText(Path.Combine(_directory.Path, "Settings.plist"), "not a plist at all");
        Assert.Null(Read());
    }

    [Fact]
    public void EmptyPlist()
    {
        Write(new());
        var result = Assert.IsType<AccountSettingsImporter.ImportedSettings>(Read());
        Assert.Null(result.Name);
        Assert.Null(result.IsActive);
        Assert.Null(result.Username);
        Assert.Null(result.LastArticleFetchStartTime);
        Assert.Null(result.LastRefreshCompletedDate);
        Assert.Null(result.EndpointUrl);
        Assert.Null(result.ExternalID);
        Assert.Null(result.ConditionalGetInfo);
    }

    [Fact]
    public void AllFields()
    {
        var fetch = ReferenceDate.AddSeconds(700_000_000);
        var end = ReferenceDate.AddSeconds(700_000_060);
        Write(new()
        {
            ["name"] = "My Account", ["isActive"] = false, ["username"] = "testuser", ["lastArticleFetch"] = fetch,
            ["lastArticleFetchEndTime"] = end, ["endpointURL"] = "https://example.com/api", ["externalID"] = "ext-42",
        });
        var result = Assert.IsType<AccountSettingsImporter.ImportedSettings>(Read());
        Assert.Equal("My Account", result.Name);
        Assert.False(result.IsActive);
        Assert.Equal("testuser", result.Username);
        Assert.Equal(fetch, result.LastArticleFetchStartTime);
        Assert.Equal(end, result.LastRefreshCompletedDate);
        Assert.Equal(new Uri("https://example.com/api"), result.EndpointUrl);
        Assert.Equal("ext-42", result.ExternalID);
    }

    [Fact]
    public void ConditionalGetInfo()
    {
        Write(new()
        {
            ["conditionalGetInfo"] = new Dictionary<string, object>
            {
                ["https://example.com/feed1"] = new Dictionary<string, object> { ["lastModified"] = "Wed, 01 Jan 2025 00:00:00 GMT", ["etag"] = "abc123" },
                ["https://example.com/feed2"] = new Dictionary<string, object> { ["etag"] = "def456" },
            },
        });
        var result = Assert.IsType<AccountSettingsImporter.ImportedSettings>(Read());
        Assert.NotNull(result.ConditionalGetInfo);
        Assert.Equal("Wed, 01 Jan 2025 00:00:00 GMT", result.ConditionalGetInfo["https://example.com/feed1"].LastModified);
        Assert.Equal("abc123", result.ConditionalGetInfo["https://example.com/feed1"].ETag);
        Assert.Null(result.ConditionalGetInfo["https://example.com/feed2"].LastModified);
        Assert.Equal("def456", result.ConditionalGetInfo["https://example.com/feed2"].ETag);
    }

    [Fact]
    public void ConditionalGetInfoBothNil()
    {
        Write(new()
        {
            ["conditionalGetInfo"] = new Dictionary<string, object> { ["https://example.com/feed"] = new Dictionary<string, object>() },
        });
        var result = Assert.IsType<AccountSettingsImporter.ImportedSettings>(Read());
        Assert.Null(result.ConditionalGetInfo?.GetValueOrDefault("https://example.com/feed"));
    }

    [Fact]
    public void EndpointURLAsDictionary()
    {
        File.WriteAllText(Path.Combine(_directory.Path, "Settings.plist"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict>
            <key>conditionalGetInfo</key><dict/>
            <key>endpointURL</key><dict><key>relative</key><string>http://example.local:8090/api/greader.php</string></dict>
            <key>isActive</key><true/>
            <key>lastArticleFetch</key><date>2026-03-14T17:01:03Z</date>
            <key>lastArticleFetchEndTime</key><date>2026-03-14T17:01:03Z</date>
            <key>username</key><string>cool-username</string>
            </dict></plist>
            """);
        var result = Assert.IsType<AccountSettingsImporter.ImportedSettings>(Read());
        Assert.Equal(new Uri("http://example.local:8090/api/greader.php"), result.EndpointUrl);
        Assert.True(result.IsActive);
        Assert.Equal("cool-username", result.Username);
        Assert.NotNull(result.LastArticleFetchStartTime);
        Assert.NotNull(result.LastRefreshCompletedDate);
        Assert.Null(result.ConditionalGetInfo);
        Assert.Null(result.Name);
        Assert.Null(result.ExternalID);
    }

    [Fact]
    public void EndpointURLAsDictionaryWithBase()
    {
        Write(new() { ["endpointURL"] = new Dictionary<string, object> { ["relative"] = "/api/greader.php", ["base"] = "http://example.local:8090" } });
        Assert.Equal("http://example.local:8090/api/greader.php", Read()?.EndpointUrl?.AbsoluteUri);
    }

    [Fact]
    public void SubsetOfFields()
    {
        Write(new() { ["name"] = "Partial Account", ["username"] = "partialuser" });
        var result = Assert.IsType<AccountSettingsImporter.ImportedSettings>(Read());
        Assert.Equal("Partial Account", result.Name);
        Assert.Equal("partialuser", result.Username);
        Assert.Null(result.IsActive);
        Assert.Null(result.LastArticleFetchStartTime);
        Assert.Null(result.LastRefreshCompletedDate);
        Assert.Null(result.EndpointUrl);
        Assert.Null(result.ExternalID);
    }

    [Fact]
    public void BinaryPropertyListIsSupported()
    {
        _directory.WritePlist("Settings.plist", new() { ["username"] = "binary-user", ["lastArticleFetch"] = ReferenceDate }, binary: true);
        Assert.Equal("binary-user", Read()?.Username);
        Assert.Equal(ReferenceDate, Read()?.LastArticleFetchStartTime);
    }

    public void Dispose() => _directory.Dispose();
}
