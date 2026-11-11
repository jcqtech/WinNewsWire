using System.Diagnostics;
using System.Text.Json;
using WinNewsWire.Articles;
using WinNewsWire.Web;
using WinNewsWire.Core;

namespace WinNewsWire.Account;

public static class FeedSettingsImporter
{
    public static void ImportIfNeeded(string dataFolder, FeedSettingsDatabase database)
    {
        if (!database.IsEmpty) return;
        var plist = PropertyListReader.ReadDictionary(Path.Combine(dataFolder, "FeedMetadata.plist"));
        if (plist is null) return;
        var rows = new List<(string Url, FeedSettingsDatabase.Row Settings)>();
        foreach (var (feedUrl, value) in plist)
        {
            if (value is not Dictionary<string, object> fields)
            {
                Trace.TraceWarning($"Skipping non-dictionary feed metadata for {feedUrl}.");
                continue;
            }
            string? Text(string key) => fields.GetValueOrDefault(key) as string;
            DateTime? Date(string key) => fields.GetValueOrDefault(key) as DateTime?;
            var headers = fields.GetValueOrDefault("conditionalGetInfo") as Dictionary<string, object>;
            var modified = headers?.GetValueOrDefault("lastModified") as string;
            var etag = headers?.GetValueOrDefault("etag") as string;
            CacheControlInfo? cache = null;
            if (fields.GetValueOrDefault("cacheControlInfo") is Dictionary<string, object> cacheFields
                && cacheFields.GetValueOrDefault("dateCreated") is DateTime created
                && cacheFields.GetValueOrDefault("maxAge") is double maxAge)
                cache = new(created, TimeSpan.FromSeconds(maxAge));
            var authors = fields.GetValueOrDefault("authors") is object[] authorValues
                ? Author.FromJson(JsonSerializer.Serialize(authorValues)) : null;
            Dictionary<string, string>? relationships = null;
            if (fields.GetValueOrDefault("folderRelationship") is Dictionary<string, object> folders && folders.Values.All(v => v is string))
                relationships = folders.ToDictionary(pair => pair.Key, pair => (string)pair.Value, StringComparer.Ordinal);
            rows.Add((feedUrl, new(Text("feedID") ?? feedUrl, Text("homePageURL"), Text("iconURL"), Text("faviconURL"),
                Text("editedName"), Text("contentHash"), fields.GetValueOrDefault("isNotifyAboutNewArticles") is true,
                fields.GetValueOrDefault("isArticleExtractorAlwaysOn") is true, authors,
                modified is null && etag is null ? null : new(modified, etag), Date("conditionalGetInfoDate"), cache,
                Text("subscriptionID"), relationships, Date("lastCheckDate"))));
        }
        database.InsertRows(rows);
    }
}
