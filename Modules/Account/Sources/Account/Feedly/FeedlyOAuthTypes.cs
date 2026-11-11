using System.Text.Json.Serialization;

namespace WinNewsWire.Feedly;

/// <summary>Port of <c>OAuthAuthorizationClient</c> (<c>OAuthAuthorizationCodeGranting.swift</c>).
/// The Id+Secret identify the Feedly-registered app; the redirect URI is where the Feedly
/// authorization server will send the browser after the user consents.</summary>
public sealed record FeedlyOAuthClient(string Id, string RedirectUri, string? State, string Secret);

/// <summary>Port of <c>OAuthAuthorizationRequest</c>. The outbound query for the authorize URL.</summary>
public sealed record FeedlyOAuthAuthorizeRequest(string ClientId, string RedirectUri, string Scope, string? State)
{
    public const string ResponseType = "code";
}

/// <summary>Port of <c>OAuthAuthorizationResponse</c>. Parsed from the redirect URI.</summary>
public sealed record FeedlyOAuthAuthorizeResponse(string Code, string? State);

/// <summary>Port of <c>OAuthAuthorizationError</c>.</summary>
public enum FeedlyOAuthAuthorizeError
{
    InvalidRequest, UnauthorizedClient, AccessDenied, UnsupportedResponseType,
    InvalidScope, ServerError, TemporarilyUnavailable, Unknown,
}

public sealed class FeedlyOAuthAuthorizeException : Exception
{
    public FeedlyOAuthAuthorizeError Error { get; }
    public string? State { get; }
    public FeedlyOAuthAuthorizeException(FeedlyOAuthAuthorizeError e, string? state, string? description)
        : base(description ?? e.ToString()) { Error = e; State = state; }
}

/// <summary>Port of <c>FeedlyOAuthAccessTokenResponse</c>. Feedly returns snake_case fields in
/// practice; we list both names (snake and camel) via <see cref="JsonPropertyName"/>.</summary>
public sealed record FeedlyOAuthAccessTokenResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("scope")] string Scope);

public static class FeedlyOAuthParser
{
    /// <summary>Port of <c>OAuthAuthorizationResponse(url:client:)</c>. Validates the redirect URI and
    /// extracts the authorization code or surfaces the error query params.</summary>
    public static FeedlyOAuthAuthorizeResponse ParseAuthorizeRedirect(Uri redirectUri, string expectedState)
    {
        var q = System.Web.HttpUtility.ParseQueryString(redirectUri.Query);
        var error = q["error"];
        if (!string.IsNullOrEmpty(error))
        {
            var mapped = error switch
            {
                "invalid_request" => FeedlyOAuthAuthorizeError.InvalidRequest,
                "unauthorized_client" => FeedlyOAuthAuthorizeError.UnauthorizedClient,
                "access_denied" => FeedlyOAuthAuthorizeError.AccessDenied,
                "unsupported_response_type" => FeedlyOAuthAuthorizeError.UnsupportedResponseType,
                "invalid_scope" => FeedlyOAuthAuthorizeError.InvalidScope,
                "server_error" => FeedlyOAuthAuthorizeError.ServerError,
                "temporarily_unavailable" => FeedlyOAuthAuthorizeError.TemporarilyUnavailable,
                _ => FeedlyOAuthAuthorizeError.Unknown,
            };
            throw new FeedlyOAuthAuthorizeException(mapped, q["state"], q["error_description"]);
        }
        var code = q["code"];
        if (string.IsNullOrEmpty(code))
            throw new FeedlyOAuthAuthorizeException(FeedlyOAuthAuthorizeError.Unknown, q["state"], "Missing 'code' in redirect.");
        var state = q["state"];
        if (!string.IsNullOrEmpty(expectedState) && state != expectedState)
            throw new FeedlyOAuthAuthorizeException(FeedlyOAuthAuthorizeError.Unknown, state, "State mismatch (possible CSRF).");
        return new FeedlyOAuthAuthorizeResponse(code, state);
    }
}
