using System.Security.Cryptography;
using System.Text.Json;

namespace WinNewsWire.Core;

public static class ArticleNotificationIdentity
{
    public static string Tag(string accountID, string articleID)
    {
        Validate(accountID, articleID);
        var data = JsonSerializer.SerializeToUtf8Bytes(new[] { accountID, articleID });
        return "art:" + Convert.ToHexString(SHA256.HashData(data))[..60];
    }

    public static Uri ActivationUri(string accountID, string articleID)
    {
        Validate(accountID, articleID);
        return new Uri("winnewswire://showunread?account=" + Uri.EscapeDataString(accountID) + "&id=" + Uri.EscapeDataString(articleID));
    }

    private static void Validate(string accountID, string articleID)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountID);
        ArgumentException.ThrowIfNullOrWhiteSpace(articleID);
    }
}
