namespace WinNewsWire.Core;

public static class SharedFeedUrl
{
    public static bool TryNormalizeUserInput(string? value, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        try { normalized = NormalizeUserInput(value); return true; }
        catch (ArgumentException) { return false; }
    }

    public static string NormalizeUserInput(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string input = value.Trim();
        if (input.Any(char.IsControl))
            throw new ArgumentException("Enter one feed or website URL at a time.", nameof(value));
        if (input.StartsWith("x-netnewswire-feed:", StringComparison.OrdinalIgnoreCase)
            || input.StartsWith("x-winnewswire-feed:", StringComparison.OrdinalIgnoreCase))
            input = "feed:" + input[(input.IndexOf(':') + 1)..];
        string address = input;
        if (address.StartsWith("feeds:", StringComparison.OrdinalIgnoreCase)) address = address[6..];
        else if (address.StartsWith("feed:", StringComparison.OrdinalIgnoreCase)) address = address[5..];
        if (address.StartsWith("//", StringComparison.Ordinal)) address = address[2..];
        int colon = address.IndexOf(':');
        bool http = UriExtensions.IsAbsoluteHttp(address);
        if (address.StartsWith(@"\\", StringComparison.Ordinal)
            || colon > 0 && Uri.CheckSchemeName(address[..colon]) && !http
                && !ushort.TryParse(address[(colon + 1)..].Split('/', '?', '#')[0], out _))
            throw new ArgumentException("A feed link cannot wrap a non-web URL.", nameof(value));
        var normalized = input.NormalizedUrl();
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
            throw new ArgumentException("An HTTP or HTTPS URL is required.", nameof(value));
        return uri.AbsoluteUri;
    }

    public static string Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string input = value.Trim();
        if (input.Any(character => char.IsControl(character)))
            throw new ArgumentException("Share one feed or website URL at a time.", nameof(value));
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https" or "feed" or "feeds" or "x-netnewswire-feed" or "x-winnewswire-feed"))
            throw new ArgumentException("The shared item must be an HTTP or HTTPS feed or website URL.", nameof(value));
        var request = AppActivationRequest.FromUri(uri);
        if (request.Kind != AppActivationKind.AddFeed || request.Target is null)
            throw new ArgumentException("The shared item is not a subscription URL.", nameof(value));
        return request.Target;
    }
}
