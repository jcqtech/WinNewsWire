namespace WinNewsWire.Web;

/// <summary>Port of <c>HTTP4xxResponse</c>. Caches 400-499 responses so the URL is skipped for a period.</summary>
internal sealed record Http4xxResponse(int StatusCode, DateTime Date)
{
    public Http4xxResponse(int statusCode) : this(statusCode, DateTime.UtcNow) { }
}
