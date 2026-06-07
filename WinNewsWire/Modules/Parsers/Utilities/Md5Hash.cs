using System.Security.Cryptography;
using System.Text;

namespace WinNewsWire.Parsers.Utilities;

internal static class Md5Hash
{
    public static string Hex(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = MD5.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
