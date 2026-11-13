using System.Security.Cryptography;
using System.Text;

namespace WinNewsWire.AppShared.ArticleRendering;

public static class ArticleImageResource
{
    public const string ResourcePath = "/article-image";
    public const string BaseUrl = "https://winnewswire.local" + ResourcePath;
    private static readonly byte[] SigningKey = RandomNumberGenerator.GetBytes(32);

    public static string Url(string imageUrl)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var image) || image.Scheme is not ("http" or "https"))
            throw new ArgumentException("An absolute HTTP image URL is required.", nameof(imageUrl));
        return BaseUrl + "?u=" + Uri.EscapeDataString(imageUrl) + "&s=" + Convert.ToHexString(Signature(imageUrl));
    }

    private static byte[] Signature(string imageUrl) => HMACSHA256.HashData(SigningKey, Encoding.UTF8.GetBytes(imageUrl));

    public static bool TryGetImageUrl(Uri requestUrl, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? imageUrl)
    {
        imageUrl = null;
        if (!requestUrl.IsAbsoluteUri || requestUrl.GetLeftPart(UriPartial.Path) != BaseUrl
            || requestUrl.Fragment.Length != 0 || !requestUrl.Query.StartsWith("?u=", StringComparison.Ordinal)) return false;
        int separator = requestUrl.Query.IndexOf("&s=", StringComparison.Ordinal);
        if (separator <= 3 || requestUrl.Query.IndexOf('&') != separator) return false;
        string signature = requestUrl.Query[(separator + 3)..];
        if (signature.Length != 64 || signature.Any(character => !Uri.IsHexDigit(character))) return false;
        string decoded = Uri.UnescapeDataString(requestUrl.Query[3..separator]);
        if (!Uri.TryCreate(decoded, UriKind.Absolute, out var original) || original.Scheme is not ("http" or "https")) return false;
        if (!CryptographicOperations.FixedTimeEquals(Signature(decoded), Convert.FromHexString(signature))) return false;
        imageUrl = decoded;
        return true;
    }
}
