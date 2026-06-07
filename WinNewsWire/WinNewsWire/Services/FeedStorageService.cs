using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using WinNewsWire.Models;

namespace WinNewsWire.Services;

/// <summary>
/// Persists feed subscriptions and read/starred state to a local JSON file.
/// </summary>
public class FeedStorageService
{
    private static readonly string _storageFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinNewsWire");

    private static readonly string _feedsFile = Path.Combine(_storageFolder, "feeds.json");
    private static readonly string _stateFile = Path.Combine(_storageFolder, "state.json");

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public FeedStorageService()
    {
        Directory.CreateDirectory(_storageFolder);
    }

    public async Task SaveFeedsAsync(List<FeedSubscription> subscriptions)
    {
        var json = JsonSerializer.Serialize(subscriptions, _jsonOptions);
        await File.WriteAllTextAsync(_feedsFile, json);
    }

    public async Task<List<FeedSubscription>> LoadFeedsAsync()
    {
        if (!File.Exists(_feedsFile))
            return GetDefaultFeeds();

        try
        {
            var json = await File.ReadAllTextAsync(_feedsFile);
            return JsonSerializer.Deserialize<List<FeedSubscription>>(json, _jsonOptions)
                   ?? GetDefaultFeeds();
        }
        catch
        {
            return GetDefaultFeeds();
        }
    }

    public async Task SaveReadStateAsync(Dictionary<string, ArticleState> state)
    {
        var json = JsonSerializer.Serialize(state, _jsonOptions);
        await File.WriteAllTextAsync(_stateFile, json);
    }

    public async Task<Dictionary<string, ArticleState>> LoadReadStateAsync()
    {
        if (!File.Exists(_stateFile))
            return new Dictionary<string, ArticleState>();

        try
        {
            var json = await File.ReadAllTextAsync(_stateFile);
            return JsonSerializer.Deserialize<Dictionary<string, ArticleState>>(json, _jsonOptions)
                   ?? new Dictionary<string, ArticleState>();
        }
        catch
        {
            return new Dictionary<string, ArticleState>();
        }
    }

    private static List<FeedSubscription> GetDefaultFeeds()
    {
        return new List<FeedSubscription>
        {
            new() { Url = "https://feeds.arstechnica.com/arstechnica/index", Folder = "News" },
            new() { Url = "https://www.theverge.com/rss/index.xml", Folder = "News" },
            new() { Url = "https://feeds.bbci.co.uk/news/rss.xml", Folder = "News" },
            new() { Url = "https://devblogs.microsoft.com/dotnet/feed/", Folder = "Programming" },
            new() { Url = "https://blog.rust-lang.org/feed.xml", Folder = "Programming" },
            new() { Url = "https://feeds.feedburner.com/TheHackersNews", Folder = "Tech" },
        };
    }
}

public class FeedSubscription
{
    public string Url { get; set; } = string.Empty;
    public string Folder { get; set; } = string.Empty;
    public string? CustomTitle { get; set; }
}

public class ArticleState
{
    public bool IsRead { get; set; }
    public bool IsStarred { get; set; }
}
