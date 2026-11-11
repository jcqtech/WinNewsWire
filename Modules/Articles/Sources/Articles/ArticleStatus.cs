namespace WinNewsWire.Articles;

/// <summary>Port of <c>ArticleStatus</c>.</summary>
public sealed class ArticleStatus : IEquatable<ArticleStatus>
{
    public const double StaleIntervalSeconds = 183 * 24 * 60 * 60;

    public enum Key { Read, Starred }

    public string ArticleID { get; }
    public DateTime DateArrived { get; }

    private readonly object _lock = new();
    private bool _read;
    private bool _starred;

    public bool Read { get { lock (_lock) return _read; } set { lock (_lock) _read = value; } }
    public bool Starred { get { lock (_lock) return _starred; } set { lock (_lock) _starred = value; } }

    public ArticleStatus(string articleID, bool read, bool starred, DateTime dateArrived)
    {
        ArticleID = articleID; _read = read; _starred = starred; DateArrived = dateArrived;
    }

    public ArticleStatus(string articleID, bool read, DateTime dateArrived)
        : this(articleID, read, false, dateArrived) { }

    public bool BoolStatus(Key key) => key == Key.Read ? Read : Starred;
    public void SetBoolStatus(bool value, Key key)
    {
        if (key == Key.Read) Read = value; else Starred = value;
    }

    public override int GetHashCode() => ArticleID.GetHashCode();
    public override bool Equals(object? obj) => obj is ArticleStatus s && Equals(s);
    public bool Equals(ArticleStatus? other)
        => other is not null && other.ArticleID == ArticleID && other.DateArrived == DateArrived
           && other.Read == Read && other.Starred == Starred;
}
