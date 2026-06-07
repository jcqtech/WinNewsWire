using System.Text.Json;

namespace WinNewsWire.Articles;

/// <summary>Port of <c>Author</c>.</summary>
public sealed class Author : IEquatable<Author>
{
    public string AuthorID { get; }
    public string? Name { get; }
    public string? Url { get; }
    public string? AvatarUrl { get; }
    public string? EmailAddress { get; }

    private Author(string id, string? name, string? url, string? avatar, string? email)
    {
        AuthorID = id; Name = name; Url = url; AvatarUrl = avatar; EmailAddress = email;
    }

    public static Author? Create(string? authorID, string? name, string? url, string? avatarUrl, string? emailAddress)
    {
        if (name is null && url is null && emailAddress is null) return null;
        var id = authorID ?? DatabaseID.For((name ?? "") + (url ?? "") + (avatarUrl ?? "") + (emailAddress ?? ""));
        return new Author(id, name, url, avatarUrl, emailAddress);
    }

    public override int GetHashCode() => AuthorID.GetHashCode();
    public override bool Equals(object? obj) => obj is Author a && Equals(a);
    public bool Equals(Author? other) => other is not null && other.AuthorID == AuthorID;

    private sealed record Dto(string authorID, string? name, string? url, string? avatarURL, string? emailAddress);

    public static HashSet<Author>? FromJson(string json)
    {
        try
        {
            var arr = JsonSerializer.Deserialize<Dto[]>(json);
            if (arr is null) return null;
            var set = new HashSet<Author>();
            foreach (var d in arr)
                set.Add(new Author(d.authorID, d.name, d.url, d.avatarURL, d.emailAddress));
            return set;
        }
        catch { return null; }
    }

    public static string? ToJson(IEnumerable<Author> authors)
    {
        var arr = authors.Select(a => new Dto(a.AuthorID, a.Name, a.Url, a.AvatarUrl, a.EmailAddress)).ToArray();
        try { return JsonSerializer.Serialize(arr); } catch { return null; }
    }
}
