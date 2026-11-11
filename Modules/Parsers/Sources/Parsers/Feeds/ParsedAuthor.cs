namespace WinNewsWire.Parsers;

/// <summary>Port of <c>ParsedAuthor</c>.</summary>
public sealed record ParsedAuthor(string? Name, string? Url, string? AvatarUrl, string? EmailAddress)
{
    public override int GetHashCode()
    {
        if (Name is not null) return Name.GetHashCode(StringComparison.Ordinal);
        if (Url is not null) return Url.GetHashCode(StringComparison.Ordinal);
        if (EmailAddress is not null) return EmailAddress.GetHashCode(StringComparison.Ordinal);
        if (AvatarUrl is not null) return AvatarUrl.GetHashCode(StringComparison.Ordinal);
        return 0;
    }

    /// <summary>Port of <c>RSParsedAuthor +authorWithSingleString:</c>.</summary>
    public static ParsedAuthor FromSingleString(string raw)
    {
        raw = raw.Trim();
        int open = raw.IndexOf('(');
        int close = raw.IndexOf(')');
        if (open > 0 && close > open)
        {
            var email = raw[..open].Trim();
            var name = raw[(open + 1)..close].Trim();
            if (email.Length > 0 && email.Contains('@'))
                return new ParsedAuthor(string.IsNullOrEmpty(name) ? null : name, null, null, email);
        }
        int lt = raw.IndexOf('<');
        int gt = raw.IndexOf('>');
        if (lt >= 0 && gt > lt)
        {
            var name = raw[..lt].Trim();
            var email = raw[(lt + 1)..gt].Trim();
            if (email.Contains('@'))
                return new ParsedAuthor(string.IsNullOrEmpty(name) ? null : name, null, null, email);
        }
        if (raw.Contains('@') && !raw.Contains(' '))
            return new ParsedAuthor(null, null, null, raw);
        return new ParsedAuthor(raw, null, null, null);
    }
}
