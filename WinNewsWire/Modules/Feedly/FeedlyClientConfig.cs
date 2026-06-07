using System.Text.Json;
using WinNewsWire.Core;

namespace WinNewsWire.Feedly;

/// <summary>Loads the OAuth client credentials Feedly requires to mint access tokens.
///
/// In NetNewsWire (Mac) these live in <c>SecretKey.feedlyClientID</c>/<c>feedlyClientSecret</c>,
/// substituted at build time from private files. We don't ship those here, so WinNewsWire reads
/// them at runtime from one of:
///
/// 1. Environment variables <c>FEEDLY_CLIENT_ID</c> and <c>FEEDLY_CLIENT_SECRET</c>.
/// 2. A JSON file <c>{DataDirectory}\feedly-client.json</c> shaped
///    <c>{"clientId":"...","clientSecret":"...","host":"cloud.feedly.com"}</c>.
///
/// When neither is present the user can still paste a pre-existing refresh token into the
/// Add-Account dialog, but interactive sign-in will throw.
/// </summary>
public sealed record FeedlyClientConfig(string ClientId, string ClientSecret, string Host = "cloud.feedly.com")
{
    public const string DefaultScope = "https://cloud.feedly.com/subscriptions";

    public static string RedirectUriDefault { get; set; } = "http://127.0.0.1:7878/feedly-callback/";

    public static FeedlyClientConfig? Load()
    {
        var id = Environment.GetEnvironmentVariable("FEEDLY_CLIENT_ID");
        var secret = Environment.GetEnvironmentVariable("FEEDLY_CLIENT_SECRET");
        var host = Environment.GetEnvironmentVariable("FEEDLY_HOST");
        if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(secret))
            return new FeedlyClientConfig(id, secret, string.IsNullOrWhiteSpace(host) ? "cloud.feedly.com" : host);

        try
        {
            var path = Path.Combine(AppConfig.DataDirectory, "feedly-client.json");
            if (File.Exists(path))
            {
                using var s = File.OpenRead(path);
                var dto = JsonSerializer.Deserialize<Dto>(s);
                if (dto is { ClientId: { Length: > 0 } cid, ClientSecret: { Length: > 0 } cs })
                    return new FeedlyClientConfig(cid, cs, string.IsNullOrWhiteSpace(dto.Host) ? "cloud.feedly.com" : dto.Host!);
            }
        }
        catch { }
        return null;
    }

    public FeedlyOAuthClient ToOAuthClient(string? state = null)
        => new(ClientId, RedirectUriDefault, state, ClientSecret);

    private sealed record Dto(string? ClientId, string? ClientSecret, string? Host);
}

public sealed class FeedlyClientConfigMissingException : Exception
{
    public FeedlyClientConfigMissingException()
        : base("Feedly OAuth client credentials are not configured. Set FEEDLY_CLIENT_ID and "
             + "FEEDLY_CLIENT_SECRET environment variables, or create feedly-client.json in the "
             + "app data directory. Alternatively, paste an existing refresh token into the "
             + "Add Account dialog.") { }
}
