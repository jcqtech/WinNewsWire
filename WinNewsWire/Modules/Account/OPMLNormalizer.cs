using WinNewsWire.Parsers;

namespace WinNewsWire.Account;

/// <summary>
/// Port of NNW's <c>OPMLNormalizer</c>.
/// Deduplicates feed URLs, promotes children of unnamed folders,
/// and flattens nested folder structures to a single level.
/// </summary>
internal static class OPMLNormalizer
{
    public static IReadOnlyList<OpmlItem> Normalize(IReadOnlyList<OpmlItem> items)
    {
        var state = new NormalizerState();
        state.Process(items, parentFolder: null);
        return state.BuildResult();
    }

    private sealed class NormalizerState
    {
        private readonly List<OpmlItem> _normalizedItems = new();

        // Tracks children added during normalization (since OpmlItem is immutable).
        private readonly Dictionary<OpmlItem, List<OpmlItem>> _extraChildren =
            new(ReferenceEqualityComparer.Instance);

        public void Process(IReadOnlyList<OpmlItem> items, OpmlItem? parentFolder)
        {
            var feedsToAdd = new List<OpmlItem>();

            foreach (var item in items)
            {
                if (item.FeedSpecifier is not null)
                {
                    if (!feedsToAdd.Any(f =>
                            string.Equals(f.FeedSpecifier?.FeedUrl, item.FeedSpecifier.FeedUrl, StringComparison.Ordinal)))
                    {
                        feedsToAdd.Add(item);
                    }
                    continue;
                }

                // Folder without a name – its items go one level up.
                if (item.Title is null)
                {
                    if (item.Children.Count > 0)
                        Process(item.Children, parentFolder);
                    continue;
                }

                // Named folder.
                feedsToAdd.Add(item);
                if (item.Children.Count > 0)
                    Process(item.Children, parentFolder ?? item);
            }

            if (parentFolder is not null)
            {
                foreach (var feed in feedsToAdd)
                {
                    if (!AllChildren(parentFolder).Any(c =>
                            string.Equals(c.FeedSpecifier?.FeedUrl, feed.FeedSpecifier?.FeedUrl, StringComparison.Ordinal)))
                    {
                        AddExtraChild(parentFolder, feed);
                    }
                }
            }
            else
            {
                _normalizedItems.AddRange(feedsToAdd);
            }
        }

        public IReadOnlyList<OpmlItem> BuildResult()
        {
            var result = new List<OpmlItem>(_normalizedItems.Count);
            foreach (var item in _normalizedItems)
                result.Add(Rebuild(item));
            return result;
        }

        private IEnumerable<OpmlItem> AllChildren(OpmlItem folder)
        {
            IEnumerable<OpmlItem> children = folder.Children;
            if (_extraChildren.TryGetValue(folder, out var extra))
                children = children.Concat(extra);
            return children;
        }

        private void AddExtraChild(OpmlItem folder, OpmlItem child)
        {
            if (!_extraChildren.TryGetValue(folder, out var list))
            {
                list = new List<OpmlItem>();
                _extraChildren[folder] = list;
            }
            list.Add(child);
        }

        private OpmlItem Rebuild(OpmlItem item)
        {
            if (item.FeedSpecifier is not null)
                return item;

            if (!_extraChildren.TryGetValue(item, out var extra) || extra.Count == 0)
                return item;

            var merged = new List<OpmlItem>(item.Children.Count + extra.Count);
            merged.AddRange(item.Children);
            merged.AddRange(extra);
            return new OpmlItem(item.Attributes, merged);
        }
    }
}
