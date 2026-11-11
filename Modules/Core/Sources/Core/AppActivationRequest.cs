namespace WinNewsWire.Core;

public enum AppActivationKind
{
    ShowMainWindow,
    AddFeed,
    ImportOpml,
    InstallThemeFile,
    InstallThemeUrl,
    ShowUnread,
    ShowToday,
    ShowStarred
}

public sealed record AppActivationRequest(
    AppActivationKind Kind, string? Target = null, string? ArticleID = null, string? AccountID = null)
{
    public static IReadOnlyList<AppActivationRequest> FromArguments(IEnumerable<string> arguments, string? workingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var values = arguments.ToArray();
        if (values.Length == 0) return [new(AppActivationKind.ShowMainWindow)];
        var result = new List<AppActivationRequest>();
        bool literal = false;
        for (int index = 0; index < values.Length; index++)
        {
            var value = values[index];
            if (!literal && value == "--") { literal = true; continue; }
            if (!literal && value is "--subscribe" or "--import-opml" or "--install-theme")
            {
                if (++index >= values.Length) throw new ArgumentException($"The {value} option requires a value.");
                string target = values[index];
                result.Add(value switch
                {
                    "--subscribe" => new(AppActivationKind.AddFeed, FeedUrl(target)),
                    "--import-opml" => FileRequest(target, workingDirectory, AppActivationKind.ImportOpml),
                    _ => IsHttp(target) ? new(AppActivationKind.InstallThemeUrl, HttpUrl(target))
                        : FileRequest(target, workingDirectory, AppActivationKind.InstallThemeFile)
                });
                continue;
            }
            if (!literal && value.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unknown activation option: {value}.");
            result.Add(FromInput(value, workingDirectory));
        }
        return result.Count == 0 ? [new(AppActivationKind.ShowMainWindow)] : result;
    }

    public static AppActivationRequest FromInput(string input, string? workingDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        string value = input.Trim();
        if (Path.IsPathFullyQualified(value) && !IsHttp(value))
            return FileRequest(value, workingDirectory);
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile) return FileRequest(uri.LocalPath, workingDirectory);
            return FromUri(uri);
        }
        return FileRequest(value, workingDirectory);
    }

    public static AppActivationRequest FromUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri) throw new ArgumentException("An activation URL must be absolute.", nameof(uri));
        switch (uri.Scheme)
        {
            case "http":
            case "https":
                return new(AppActivationKind.AddFeed, HttpUrl(uri.AbsoluteUri));
            case "feed":
            case "feeds":
                return new(AppActivationKind.AddFeed, FeedUrl(uri.OriginalString));
            case "x-netnewswire-feed":
            case "x-winnewswire-feed":
                return new(AppActivationKind.AddFeed, FeedUrl("feed:" + uri.OriginalString[(uri.OriginalString.IndexOf(':') + 1)..]));
            case "netnewswire":
            case "winnewswire":
            case "nnw":
                return FromApplicationUri(uri);
            default:
                throw new ArgumentException("This URL is not a supported feed or application link.", nameof(uri));
        }
    }

    public static AppActivationKind FileKind(string filename) => Path.GetExtension(filename).ToLowerInvariant() switch
    {
        ".opml" or ".xml" => AppActivationKind.ImportOpml,
        ".nnwtheme" or ".zip" => AppActivationKind.InstallThemeFile,
        _ => throw new ArgumentException("Only OPML/XML subscriptions or .nnwtheme/ZIP themes can be opened.", nameof(filename))
    };

    public static AppActivationRequest FromNotification(IReadOnlyDictionary<string, string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!arguments.TryGetValue("articleID", out var article) || string.IsNullOrWhiteSpace(article))
            return new(AppActivationKind.ShowMainWindow);
        if (!arguments.TryGetValue("accountID", out var account) || string.IsNullOrWhiteSpace(account))
            throw new ArgumentException("The article notification does not identify an account.", nameof(arguments));
        return new(AppActivationKind.ShowUnread, ArticleID: article, AccountID: account);
    }

    private static AppActivationRequest FileRequest(string input, string? workingDirectory, AppActivationKind? expected = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        var kind = FileKind(input);
        if (expected is { } required && kind != required)
            throw new ArgumentException("The file type does not match the requested operation.", nameof(input));
        if (!Path.IsPathFullyQualified(input) && workingDirectory is null)
            throw new ArgumentException("Use an absolute file path when opening an item in an already-running instance.", nameof(input));
        string path = Path.GetFullPath(input, workingDirectory ?? Environment.CurrentDirectory);
        return new(kind, path);
    }

    private static AppActivationRequest FromApplicationUri(Uri uri)
    {
        if (uri.UserInfo.Length != 0 || !uri.IsDefaultPort || uri.AbsolutePath is not ("" or "/"))
            throw new ArgumentException("The application link has an unsupported address or path.", nameof(uri));
        var query = QueryItems(uri);
        string? id = Value("id");
        string? account = Value("account");
        return uri.Host.ToLowerInvariant() switch
        {
            "theme" => new(AppActivationKind.InstallThemeUrl, HttpUrl(Required("url"))),
            "subscribe" => new(AppActivationKind.AddFeed, FeedUrl(Required("url"))),
            "showunread" => new(AppActivationKind.ShowUnread, ArticleID: id, AccountID: account),
            "showtoday" => new(AppActivationKind.ShowToday, ArticleID: id, AccountID: account),
            "showstarred" => new(AppActivationKind.ShowStarred, ArticleID: id, AccountID: account),
            "icon" or "open" => new(AppActivationKind.ShowMainWindow),
            _ => throw new ArgumentException("The application link specifies an unknown action.", nameof(uri))
        };

        string? Value(string key) => query.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : null;
        string Required(string key) => Value(key) ?? throw new ArgumentException($"The application link requires {key}.");
    }

    private static Dictionary<string, string> QueryItems(Uri uri)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string original = uri.OriginalString;
        int start = original.IndexOf('?');
        int fragment = original.IndexOf('#');
        if (start < 0 || fragment >= 0 && fragment < start) return result;
        var query = original[(start + 1)..(fragment < 0 ? original.Length : fragment)];
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            string key = Decode(equals >= 0 ? pair[..equals] : pair);
            string value = Decode(equals >= 0 ? pair[(equals + 1)..] : "");
            if (!result.TryAdd(key, value)) throw new ArgumentException("The application link contains duplicate query parameters.", nameof(uri));
        }
        return result;
    }

    private static string Decode(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] != '%') continue;
            if (index + 2 >= value.Length || !Uri.IsHexDigit(value[index + 1]) || !Uri.IsHexDigit(value[index + 2]))
                throw new ArgumentException("The application link contains an invalid percent escape.");
            index += 2;
        }
        return Uri.UnescapeDataString(value);
    }

    private static bool IsHttp(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private static string HttpUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
            throw new ArgumentException("An HTTP or HTTPS URL is required.", nameof(value));
        return uri.AbsoluteUri;
    }

    private static string FeedUrl(string value) => SharedFeedUrl.NormalizeUserInput(value);
}
