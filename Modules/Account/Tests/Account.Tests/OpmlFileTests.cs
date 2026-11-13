using System;
using System.IO;
using System.Linq;
using System.Text;
using WinNewsWire.Account;
using WinNewsWire.Core;
using WinNewsWire.Parsers;
using Xunit;

namespace WinNewsWire.Account.Tests;

/// <summary>
/// Tests for <see cref="OpmlFile"/> and the related batch-load path on
/// <see cref="Account"/>. Mirrors the Mac app's import → save → reload round
/// trip so we know Subscriptions.opml survives an external edit cleanly.
/// </summary>
public class OpmlFileTests : IDisposable
{
    private readonly string _tempDir;
    private readonly System.Collections.Generic.List<Account> _accounts = new();

    public OpmlFileTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "WinNewsWire.OpmlTests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        // Redirect AppConfig so the Account constructor doesn't write into the
        // user's real LocalAppData.
        AppConfig.SetDataDirectoryOverride(_tempDir);
    }

    public void Dispose()
    {
        foreach (var account in _accounts) account.Dispose();
        AppConfig.SetDataDirectoryOverride(null);
        Directory.Delete(_tempDir, recursive: true);
    }

    private Account MakeLocalAccount(string id = "test-local")
    {
        var account = new Account(id, AccountType.OnMyMac, "Test", new LocalAccountDelegate());
        _accounts.Add(account);
        return account;
    }

    [Fact]
    public void OpmlFileIsCreatedForLocalAccount()
    {
        var account = MakeLocalAccount();
        Assert.NotNull(account.OpmlFile);
        Assert.NotNull(account.OpmlFile!.FilePath);
        Assert.EndsWith("Subscriptions.opml", account.OpmlFile.FilePath);
    }

    [Fact]
    public void BatchUpdateFiresStructureChangeOnce()
    {
        var account = MakeLocalAccount();
        int structureChangedCount = 0;
        account.AccountStructureChanged += (_, _) => structureChangedCount++;

        account.PerformBatchUpdate(() =>
        {
            account.AddFeed("https://a.example/feed");
            account.AddFeed("https://b.example/feed");
            account.AddFolder("Folder1");
            account.AddFolder("Folder2");
        });

        // 1 event for the batch, not 4.
        Assert.Equal(1, structureChangedCount);
        Assert.Equal(2, account.TopLevelFeeds.Count);
        Assert.Equal(2, account.Folders.Count);
    }

    [Fact]
    public void OpmlFileLoadAppliesParsedOutlines()
    {
        var account = MakeLocalAccount();
        var path = account.OpmlFile!.FilePath;

        File.WriteAllText(path, OpmlFixture(
            ("Phil's Site",   "https://phil.example/feed.xml", null),
            ("Tech News",     "https://tech.example/rss",      null),
            ("Friends",       null,                             new[] {
                ("Alice's Blog", "https://alice.example/feed"),
                ("Bob's Blog",   "https://bob.example/feed"),
            })),
            Encoding.UTF8);

        account.OpmlFile.Load();

        Assert.Equal(2, account.TopLevelFeeds.Count);
        Assert.Single(account.Folders);
        Assert.Equal("Friends", account.Folders[0].Name);
        Assert.Equal(2, account.Folders[0].Feeds.Count);
    }

    [Fact]
    public void OpmlFileLoadIsIdempotentForExistingFeeds()
    {
        var account = MakeLocalAccount();
        var path = account.OpmlFile!.FilePath;
        File.WriteAllText(path, OpmlFixture(
            ("Alpha", "https://alpha.example/feed", null),
            ("Beta",  "https://beta.example/feed",  null)),
            Encoding.UTF8);

        account.OpmlFile.Load();
        Assert.Equal(2, account.TopLevelFeeds.Count);

        // Load again — should not duplicate.
        account.OpmlFile.Load();
        Assert.Equal(2, account.TopLevelFeeds.Count);
    }

    [Fact]
    public void OpmlFileAutoSavesOnStructureChange()
    {
        var account = MakeLocalAccount();
        var path = account.OpmlFile!.FilePath;
        // Ensure clean slate.
        if (File.Exists(path)) File.Delete(path);

        account.AddFeed("https://example.com/feed");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("example.com/feed", text);
        Assert.Contains("opml", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FeedTitleChangesAreSavedWithoutAStructureChange(bool inFolder)
    {
        var account = MakeLocalAccount();
        var feed = account.AddFeed("https://example.test/rss", "Old title", inFolder ? account.AddFolder("Podcasts") : null);
        feed.Name = "New & updated title";
        feed.OnDisplayNameChanged();
        var document = OpmlParser.Parse(new ParserData(account.OpmlFile!.FilePath, File.ReadAllBytes(account.OpmlFile.FilePath)));
        var outline = inFolder ? Assert.Single(Assert.Single(document.Children).Children) : Assert.Single(document.Children);
        Assert.Equal("New & updated title", outline.Title);
        account.Dispose();
        _accounts.Remove(account);
        Assert.Equal("New & updated title", Assert.Single(MakeLocalAccount().FlattenedFeeds()).Name);
    }

    [Fact]
    public void OpmlFileLoadIgnoresInvalidXml()
    {
        var account = MakeLocalAccount();
        var path = account.OpmlFile!.FilePath;
        File.WriteAllText(path, "<not opml/>", Encoding.UTF8);
        // Should not throw.
        account.OpmlFile.Load();
        Assert.Empty(account.TopLevelFeeds);
    }

    [Fact]
    public void AccountBehaviorsExposeServiceCapabilities()
    {
        Assert.Equal(AccountBehavior.None, AccountBehaviors.For(AccountType.NewsBlur));
        Assert.True(AccountType.Feedly.Has(AccountBehavior.DisallowFeedInRootFolder));
        Assert.False(AccountType.Feedly.Has(AccountBehavior.DisallowFeedRename));
        Assert.True(AccountType.Feedbin.Has(AccountBehavior.DisallowFeedCopyInRootFolder));
        Assert.False(AccountType.Feedbin.Has(AccountBehavior.DisallowFeedInMultipleFolders));
        Assert.True(AccountType.FreshRSS.Has(AccountBehavior.DisallowFeedInRootFolder));
        Assert.False(AccountType.OnMyMac.Has(AccountBehavior.DisallowFeedInRootFolder));
        Assert.False(AccountType.OnMyMac.Has(AccountBehavior.DisallowFolderManagement));
    }

    private static string OpmlFixture(params (string title, string? feedUrl, (string title, string url)[]? children)[] items)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        sb.AppendLine("""<opml version="1.1">""");
        sb.AppendLine("<head><title>Test</title></head>");
        sb.AppendLine("<body>");
        foreach (var (title, feedUrl, children) in items)
        {
            if (feedUrl is not null)
            {
                sb.AppendLine($"<outline text=\"{title}\" title=\"{title}\" type=\"rss\" xmlUrl=\"{feedUrl}\"/>");
            }
            else
            {
                sb.AppendLine($"<outline text=\"{title}\" title=\"{title}\">");
                if (children is not null)
                {
                    foreach (var (ctitle, curl) in children)
                        sb.AppendLine($"  <outline text=\"{ctitle}\" title=\"{ctitle}\" type=\"rss\" xmlUrl=\"{curl}\"/>");
                }
                sb.AppendLine("</outline>");
            }
        }
        sb.AppendLine("</body>");
        sb.AppendLine("</opml>");
        return sb.ToString();
    }
}
