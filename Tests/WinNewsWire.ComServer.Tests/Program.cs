using System.Text.Json;
using System.Runtime.InteropServices;
using WinNewsWire.Account;
using WinNewsWire.AppShared.Scripting;
using WinNewsWire.Articles;
using WinNewsWire.Automation;
using WinNewsWire.Core;

namespace WinNewsWire.ComServer.Tests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--typed-client") return RunTypedClient(args[1]);
        if (args.LastOrDefault()?.Equals("-Embedding", StringComparison.OrdinalIgnoreCase) == true) args = args[..^1];
        if (args.Length is not (4 or 6) || args[0] != "--fixture") return 2;
        int articleCount = 1;
        if (args.Length == 6 && (args[4] != "--articles" || !int.TryParse(args[5], out articleCount)
            || articleCount is < 1 or > 100000)) return 2;
        LocalServer.InitializeSecurity();
        File.WriteAllText(args[3], Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var directory = Path.Combine(Path.GetTempPath(), "WinNewsWire-ComFixture-" + Guid.Parse(args[2]).ToString("N"));
        AppConfig.SetDataDirectoryOverride(directory);
        try { return Run(args[1], Guid.Parse(args[2]), articleCount); }
        finally
        {
            AppConfig.SetDataDirectoryOverride(null);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static int RunTypedClient(string progid)
    {
        var application = (IApplication)Activator.CreateInstance(Type.GetTypeFromProgID(progid, throwOnError: true)!)!;
        var accounts = (IObjectCollection)application.Accounts;
        var account = (IAccount)accounts[1];
        var feeds = (IObjectCollection)account.Feeds;
        var feed = (IFeed)feeds[1];
        var articles = (IObjectCollection)feed.Articles;
        var article = (IArticle)articles.ByID("publisher-id");
        try
        {
            if (application.Name != "WinNewsWire" || account.ID != "fixture-account" || feed.Name != "Fixture Feed"
                || article.ID != "publisher-id" || article.PublishedDate is not DateTime)
                throw new InvalidOperationException("The typed COM interfaces returned incorrect data.");
            article.Starred = true;
            if (!article.Starred) throw new InvalidOperationException("The typed property setter did not persist.");
            Console.WriteLine("COM PARITY OK");
        }
        finally
        {
            foreach (var value in new object[] { article, articles, feed, feeds, account, accounts, application })
                Marshal.FinalReleaseComObject(value);
        }
        return 0;
    }

    private static int Run(string pipeName, Guid classID, int articleCount)
    {
        using var account = new Account.Account("fixture-account", AccountType.OnMyMac, "Fixture Account", new FixtureDelegate());
        var feed = account.AddFeed("https://example.test/rss", "Fixture Feed");
        var author = Author.Create("fixture-author", "Fixture Author", "https://example.test/author",
            "https://example.test/avatar.png", "author@example.test")!;
        feed.Authors = new HashSet<Author> { author };
        feed.HomePageUrl = "https://example.test/";
        feed.IconUrl = "https://example.test/icon.png";
        feed.FaviconUrl = "https://example.test/favicon.ico";
        var folder = account.AddFolder("Fixture Folder");
        account.AddFeed("https://example.test/nested", "Nested Feed", folder);
        var date = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        var article = new Article(account.AccountID, "database-id", feed.FeedID, "publisher-id", "Fixture Article",
            "<p>Body</p>", "Body", null, "https://example.test/article", "https://example.test/external",
            "Summary", "https://example.test/image.png", date, date, new HashSet<Author> { author }, null,
            new ArticleStatus("database-id", false, false, date));
        account.Database.UpdateArticlesAsync([article]).GetAwaiter().GetResult();
        if (articleCount > 1)
        {
            string body = "<p>" + new string('x', 4096) + "</p>";
            var articles = Enumerable.Range(1, articleCount - 1).Select(index =>
                new Article(account.AccountID, "database-" + index, feed.FeedID, "publisher-" + index,
                    "Sample Article " + index, body, "Sample body " + index, null, "https://example.test/article/" + index,
                    null, "Sample summary", null, date.AddMinutes(index), null, null, null,
                    new ArticleStatus("database-" + index, index % 2 == 0, false, date)));
            account.Database.UpdateArticlesAsync(articles).GetAwaiter().GetResult();
        }
        var service = new ScriptingService(() => [account], () => account,
            () => new(article, [article], [(feed, null)]), _ => { }, "9.8.7");
        var endpoint = new LocalAutomationPipe(pipeName, async (json, ct) =>
        {
            var response = await service.HandleAsync(ScriptingProtocol.Parse(json), ct);
            return JsonSerializer.Serialize(response, ScriptingProtocol.Options);
        }, error => Console.Error.WriteLine(error));
        endpoint.StartAsync().GetAwaiter().GetResult();
        try
        {
            LocalServer.Run(new PipeTransport(pipeName, () => throw new InvalidOperationException("The fixture pipe stopped.")),
                classID, idleMilliseconds: 2000);
        }
        finally { endpoint.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        return 0;
    }

    private sealed class FixtureDelegate : IAccountDelegate
    {
        private readonly LocalAccountDelegate _local = new();
        public AccountType Type => AccountType.OnMyMac;
        public Task RefreshAllAsync(Account.Account account, IProgress<ProgressInfo>? progress, CancellationToken ct) => Task.CompletedTask;
        public Task<Feed?> CreateFeedAsync(Account.Account account, string url, string? name, Folder? folder, CancellationToken ct)
            => Task.FromResult<Feed?>(account.AddFeed(url, name, folder));
        public Task<Folder> CreateFolderAsync(Account.Account account, string name, CancellationToken ct = default)
            => _local.CreateFolderAsync(account, name, ct);
        public Task RemoveFeedAsync(Account.Account account, Feed feed, Folder? folder, CancellationToken ct = default)
            => _local.RemoveFeedAsync(account, feed, folder, ct);
        public Task RemoveFolderAsync(Account.Account account, Folder folder, CancellationToken ct = default)
            => _local.RemoveFolderAsync(account, folder, ct);
    }
}
