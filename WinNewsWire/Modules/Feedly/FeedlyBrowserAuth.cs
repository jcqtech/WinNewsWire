using System.Diagnostics;
using System.Net;
using System.Text;
using System.Web;

namespace WinNewsWire.Feedly;

/// <summary>
/// Windows replacement for Apple's <c>ASWebAuthenticationSession</c>. Opens the system browser
/// pointed at Feedly's consent URL, then blocks on a local <see cref="HttpListener"/> until the
/// browser is redirected back to <c>http://127.0.0.1:{port}/feedly-callback/</c>. This is the
/// "Loopback Interface Redirection" pattern recommended by RFC 8252 for native apps.
/// </summary>
public static class FeedlyBrowserAuth
{
    /// <summary>Full Feedly sign-in round-trip: launch browser → wait for redirect →
    /// exchange code for access+refresh tokens.</summary>
    /// <exception cref="FeedlyClientConfigMissingException"></exception>
    /// <exception cref="FeedlyOAuthAuthorizeException"></exception>
    public static async Task<FeedlyOAuthAccessTokenResponse> SignInAsync(
        FeedlyClientConfig config,
        HttpClient? http = null,
        CancellationToken ct = default)
    {
        var state = Guid.NewGuid().ToString("N");
        var client = config.ToOAuthClient(state);
        var authorizeUri = BuildAuthorizeUri(config.Host, new FeedlyOAuthAuthorizeRequest(
            ClientId: client.Id,
            RedirectUri: client.RedirectUri,
            Scope: FeedlyClientConfig.DefaultScope,
            State: state));

        // Reserve the loopback listener BEFORE launching the browser so we don't miss fast callbacks.
        var redirectUri = new Uri(client.RedirectUri);
        using var listener = new HttpListener();
        listener.Prefixes.Add(client.RedirectUri);
        listener.Start();

        try { Process.Start(new ProcessStartInfo(authorizeUri.ToString()) { UseShellExecute = true }); }
        catch (Exception ex) { throw new InvalidOperationException("Could not launch browser for Feedly sign-in.", ex); }

        var ctx = await listener.GetContextAsync().WaitAsync(ct);
        var callback = ctx.Request.Url!;
        await WriteHtmlResponseAsync(ctx.Response,
            "<html><body style='font-family:system-ui;padding:2em'><h2>Sign-in complete</h2>"
          + "<p>You can close this tab and return to WinNewsWire.</p></body></html>");

        var authResponse = FeedlyOAuthParser.ParseAuthorizeRedirect(callback, state);
        return await ExchangeCodeForTokenAsync(config, authResponse.Code, http, ct);
    }

    public static Uri BuildAuthorizeUri(string host, FeedlyOAuthAuthorizeRequest req)
    {
        var b = new StringBuilder();
        b.Append("https://").Append(host).Append("/v3/auth/auth?");
        b.Append("response_type=").Append(FeedlyOAuthAuthorizeRequest.ResponseType);
        b.Append("&client_id=").Append(Uri.EscapeDataString(req.ClientId));
        b.Append("&scope=").Append(Uri.EscapeDataString(req.Scope));
        b.Append("&redirect_uri=").Append(Uri.EscapeDataString(req.RedirectUri));
        if (!string.IsNullOrEmpty(req.State)) b.Append("&state=").Append(Uri.EscapeDataString(req.State!));
        return new Uri(b.ToString());
    }

    public static async Task<FeedlyOAuthAccessTokenResponse> ExchangeCodeForTokenAsync(
        FeedlyClientConfig config, string code, HttpClient? http = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = FeedlyClientConfig.RedirectUriDefault,
            ["client_id"] = config.ClientId,
            ["client_secret"] = config.ClientSecret,
            ["scope"] = FeedlyClientConfig.DefaultScope,
        };
        return await PostTokenAsync(config, body, http, ct);
    }

    public static async Task<FeedlyOAuthAccessTokenResponse> RefreshTokenAsync(
        FeedlyClientConfig config, string refreshToken, HttpClient? http = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = config.ClientId,
            ["client_secret"] = config.ClientSecret,
        };
        return await PostTokenAsync(config, body, http, ct);
    }

    private static async Task<FeedlyOAuthAccessTokenResponse> PostTokenAsync(
        FeedlyClientConfig config, Dictionary<string, string> body, HttpClient? http, CancellationToken ct)
    {
        var ownedHttp = http is null;
        http ??= new HttpClient();
        try
        {
            var url = $"https://{config.Host}/v3/auth/token";
            // Feedly accepts form-encoded bodies on this endpoint; JSON would also work but
            // form-encoding is interoperable with more proxies.
            using var content = new FormUrlEncodedContent(body);
            using var resp = await http.PostAsync(url, content, ct);
            var payload = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Feedly token endpoint {(int)resp.StatusCode}: {payload}");
            var parsed = System.Text.Json.JsonSerializer.Deserialize<FeedlyOAuthAccessTokenResponse>(payload)
                ?? throw new InvalidOperationException("Feedly returned an empty token response.");
            return parsed;
        }
        finally { if (ownedHttp) http.Dispose(); }
    }

    private static async Task WriteHtmlResponseAsync(HttpListenerResponse response, string html)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        response.StatusCode = 200;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.OutputStream.Close();
    }
}
