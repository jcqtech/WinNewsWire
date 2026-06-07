namespace WinNewsWire.Web;

/// <summary>Port of <c>HTTPResponse429</c>. Tracks per-host 429 Too Many Requests responses.</summary>
internal sealed class HttpResponse429
{
    public Uri Url { get; }
    public string Host { get; }
    public DateTime DateCreated { get; }
    public TimeSpan RetryAfter { get; }

    public DateTime ResumeDate => DateCreated + RetryAfter;
    public bool CanResume => DateTime.UtcNow >= ResumeDate;

    private HttpResponse429(Uri url, string host, TimeSpan retryAfter)
    {
        Url = url;
        Host = host;
        DateCreated = DateTime.UtcNow;
        RetryAfter = retryAfter;
    }

    public static HttpResponse429? Create(Uri url, TimeSpan retryAfter)
    {
        var host = url.Host;
        if (string.IsNullOrEmpty(host)) return null;
        if (retryAfter <= TimeSpan.Zero) return null;
        return new HttpResponse429(url, host.ToLowerInvariant(), retryAfter);
    }
}
