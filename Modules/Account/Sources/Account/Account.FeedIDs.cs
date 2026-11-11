namespace WinNewsWire.Account;

public sealed partial class Account
{
    private readonly Dictionary<string, string> _feedIDRedirects = new(StringComparer.Ordinal);

    internal async Task RepairFeedIDsAsync(IEnumerable<(Feed Feed, string ID)> repairs)
    {
        var replacements = repairs.Where(pair => pair.Feed.FeedID != pair.ID)
            .DistinctBy(pair => pair.Feed.FeedID).ToDictionary(pair => pair.Feed, pair => pair.Feed.WithFeedID(pair.ID));
        if (replacements.Count == 0) return;
        var previousRedirects = new Dictionary<string, string>(_feedIDRedirects, StringComparer.Ordinal);
        foreach (var (before, after) in replacements)
        {
            foreach (var key in _feedIDRedirects.Where(p => p.Value == before.FeedID).Select(p => p.Key).ToList())
                _feedIDRedirects[key] = after.FeedID;
            _feedIDRedirects[before.FeedID] = after.FeedID;
        }
        ReplaceFeeds(replacements);
        try
        {
            // Persist the replayable mapping before touching article rows, so a crash
            // between the two stores is completed when the account is reopened.
            SaveToDisk();
        }
        catch
        {
            ReplaceFeeds(replacements.ToDictionary(pair => pair.Value, pair => pair.Key));
            _feedIDRedirects.Clear();
            foreach (var pair in previousRedirects) _feedIDRedirects.Add(pair.Key, pair.Value);
            throw;
        }
        await Database.RemapFeedIDsAsync(_feedIDRedirects);
        SaveAndNotifyStructure();
    }

    private void ReplaceFeeds(IReadOnlyDictionary<Feed, Feed> replacements)
    {
        foreach (var container in Folders.Select(folder => folder.Feeds).Prepend(TopLevelFeeds))
            for (int i = 0; i < container.Count; i++)
                if (replacements.TryGetValue(container[i], out var replacement))
                {
                    container[i] = replacement;
                    replacement.Settings.Attach(replacement);
                }
    }
}
