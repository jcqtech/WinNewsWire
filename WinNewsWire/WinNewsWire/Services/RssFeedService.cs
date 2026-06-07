using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.ServiceModel.Syndication;
using System.Threading.Tasks;
using System.Xml;
using WinNewsWire.Models;

namespace WinNewsWire.Services;

public class RssFeedService
{
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    static RssFeedService()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "WinNewsWire/1.0 (Windows; RSS Reader)");
    }

    public async Task<Feed> FetchFeedAsync(string feedUrl)
    {
        var feed = new Feed { FeedUrl = feedUrl };
        try
        {
            using var stream = await _httpClient.GetStreamAsync(feedUrl);
            using var reader = XmlReader.Create(stream);
            var syndicationFeed = SyndicationFeed.Load(reader);

            feed.Title = syndicationFeed.Title?.Text ?? feedUrl;
            feed.Description = syndicationFeed.Description?.Text ?? string.Empty;
            feed.SiteUrl = syndicationFeed.Links
                .FirstOrDefault(l => l.RelationshipType == "alternate")?.Uri?.ToString()
                ?? syndicationFeed.Links.FirstOrDefault()?.Uri?.ToString()
                ?? string.Empty;

            if (syndicationFeed.ImageUrl != null)
                feed.IconUrl = syndicationFeed.ImageUrl.ToString();

            foreach (var item in syndicationFeed.Items)
            {
                var feedItem = ConvertToFeedItem(item, feed);
                feed.Items.Add(feedItem);
            }

            feed.UnreadCount = feed.Items.Count(i => !i.IsRead);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error fetching feed {feedUrl}: {ex.Message}");
        }

        return feed;
    }

    public async Task RefreshFeedAsync(Feed feed)
    {
        try
        {
            using var stream = await _httpClient.GetStreamAsync(feed.FeedUrl);
            using var reader = XmlReader.Create(stream);
            var syndicationFeed = SyndicationFeed.Load(reader);

            var existingIds = new HashSet<string>(feed.Items.Select(i => i.Link));

            foreach (var item in syndicationFeed.Items)
            {
                var link = item.Links.FirstOrDefault()?.Uri?.ToString() ?? string.Empty;
                if (!existingIds.Contains(link))
                {
                    var feedItem = ConvertToFeedItem(item, feed);
                    feed.Items.Insert(0, feedItem);
                }
            }

            feed.UnreadCount = feed.Items.Count(i => !i.IsRead);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error refreshing feed {feed.FeedUrl}: {ex.Message}");
        }
    }

    private static FeedItem ConvertToFeedItem(SyndicationItem item, Feed parentFeed)
    {
        var content = string.Empty;
        if (item.Content is TextSyndicationContent textContent)
            content = textContent.Text;
        else if (item.Summary != null)
            content = item.Summary.Text;

        var summary = item.Summary?.Text ?? string.Empty;
        if (summary.Length > 300)
            summary = summary[..300] + "…";
        // Strip HTML tags for plain-text summary
        summary = System.Text.RegularExpressions.Regex.Replace(summary, "<[^>]+>", " ");
        summary = System.Text.RegularExpressions.Regex.Replace(summary, @"\s+", " ").Trim();

        var imageUrl = ExtractImageUrl(item, content);

        return new FeedItem
        {
            Title = item.Title?.Text ?? "(No title)",
            Summary = summary,
            Content = content,
            Link = item.Links.FirstOrDefault()?.Uri?.ToString() ?? string.Empty,
            Author = item.Authors.FirstOrDefault()?.Name
                     ?? item.Authors.FirstOrDefault()?.Email
                     ?? string.Empty,
            PublishedDate = item.PublishDate != DateTimeOffset.MinValue
                ? item.PublishDate
                : item.LastUpdatedTime,
            ImageUrl = imageUrl,
            FeedId = parentFeed.Id,
            FeedTitle = parentFeed.Title,
            FeedIconUrl = parentFeed.IconUrl
        };
    }

    private static string ExtractImageUrl(SyndicationItem item, string content)
    {
        // Try media:thumbnail or enclosure
        foreach (var ext in item.ElementExtensions)
        {
            if (ext.OuterName == "thumbnail")
            {
                var element = ext.GetObject<XmlElement>();
                var url = element?.GetAttribute("url");
                if (!string.IsNullOrEmpty(url)) return url;
            }
        }

        foreach (var link in item.Links)
        {
            if (link.RelationshipType == "enclosure" &&
                link.MediaType?.StartsWith("image") == true)
            {
                return link.Uri?.ToString() ?? string.Empty;
            }
        }

        // Try extracting first <img> from content
        if (!string.IsNullOrEmpty(content))
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                content, @"<img[^>]+src=[""']([^""']+)[""']", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
                return match.Groups[1].Value;
        }

        return string.Empty;
    }
}
