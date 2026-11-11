namespace WinNewsWire.Articles;

public sealed class AuthorCache
{
    public static AuthorCache Shared { get; } = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, Author> _authors = new(StringComparer.Ordinal);

    public HashSet<Author> Add(IEnumerable<Author> authors)
    {
        lock (_gate)
        {
            var result = new HashSet<Author>();
            foreach (var author in authors)
            {
                if (!_authors.TryGetValue(author.AuthorID, out var existing))
                    _authors.Add(author.AuthorID, existing = author);
                result.Add(existing);
            }
            return result;
        }
    }

    public int Count { get { lock (_gate) return _authors.Count; } }
    public void Clear() { lock (_gate) _authors.Clear(); }
}
