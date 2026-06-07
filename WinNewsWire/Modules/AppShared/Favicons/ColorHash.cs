using System.Security.Cryptography;
using System.Text;

namespace WinNewsWire.AppShared.Favicons;

/// <summary>Port of <c>ColorHash</c>. Deterministic color from a string.</summary>
public static class ColorHash
{
    public static (byte R, byte G, byte B) ColorForString(string s)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(s));
        return (bytes[0], bytes[1], bytes[2]);
    }
}
