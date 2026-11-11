namespace WinNewsWire.Web;

/// <summary>Port of RSWeb's HTTPLinkPagingInfo.</summary>
public sealed record HttpLinkPagingInfo(string? NextPage, string? LastPage)
{
    public static HttpLinkPagingInfo FromResponse(HttpResponseMessage response)
    {
        string? next = null, last = null;
        if (response.Headers.TryGetValues("Link", out var values))
        {
            foreach (var link in values.SelectMany(value => value.Split(',')))
            {
                var components = link.Split("; ", StringSplitOptions.None);
                if (components.Length < 2) continue;
                var page = components[0].Trim(' ', '<', '>');
                if (components[1] == "rel=\"next\"") next = page;
                else if (components[1] == "rel=\"last\"") last = page;
            }
        }
        return new(next, last);
    }
}
