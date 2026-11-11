using System.Net;

namespace WinNewsWire.Web;

/// <summary>TestingURLProtocol's canned responses and request history, scoped to an explicitly injected handler.</summary>
public sealed class TestingHttpMessageHandler : HttpMessageHandler
{
    public sealed record Response(HttpStatusCode StatusCode = HttpStatusCode.OK, byte[]? Data = null);

    private readonly object _gate = new();
    private readonly Dictionary<(string UrlSubstring, string? Method), Response> _responses = new();
    private readonly List<(string Url, string Method)> _requests = new();
    private bool _disposed;

    public void SetResponse(Response response, string forUrlContaining, HttpMethod? httpMethod = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(forUrlContaining);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _responses[(forUrlContaining, httpMethod?.Method)] = response with { Data = response.Data?.ToArray() };
        }
    }

    public int RequestCount(string forUrlContaining, HttpMethod? httpMethod = null)
    {
        ArgumentNullException.ThrowIfNull(forUrlContaining);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _requests.Count(request => request.Url.Contains(forUrlContaining, StringComparison.Ordinal)
                && (httpMethod is null || request.Method == httpMethod.Method));
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _responses.Clear();
            _requests.Clear();
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var url = request.RequestUri?.AbsoluteUri ?? throw new ArgumentException("A fixture request must have an absolute URL.", nameof(request));
            _requests.Add((url, request.Method.Method));
            var response = FindResponse(url, request.Method.Method) ?? FindResponse(url, null);
            // The native fixture deliberately answers unstubbed requests with empty HTTP 200 responses.
            return Task.FromResult(new HttpResponseMessage(response?.StatusCode ?? HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(response?.Data?.ToArray() ?? [])
            });
        }
    }

    private Response? FindResponse(string url, string? method)
        => _responses.FirstOrDefault(entry => entry.Key.Method == method
            && url.Contains(entry.Key.UrlSubstring, StringComparison.Ordinal)).Value;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
            {
                _disposed = true;
                _responses.Clear();
                _requests.Clear();
            }
        }
        base.Dispose(disposing);
    }
}
