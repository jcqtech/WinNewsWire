using System.Buffers;
using System.Text;
using WinNewsWire.Parsers.Xml;

namespace WinNewsWire.Parsers.Utilities;

/// <summary>Port of <c>NSString (RSParser)</c> helpers used by the feed parsers.</summary>
public static class StringExtensions
{
    public static string? NilIfEmptyOrWhitespace(this string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s;

    public static string DecodeHtmlEntities(this string s)
    {
        if (!s.Contains('&')) return s;
        var bytes = Encoding.UTF8.GetBytes(s);
        var output = new ArrayBufferWriter<byte>(bytes.Length);
        var position = 0;
        while (position < bytes.Length)
        {
            var next = bytes.AsSpan(position).IndexOf(XmlAscii.Ampersand);
            if (next < 0) { output.Write(bytes.AsSpan(position)); break; }
            output.Write(bytes.AsSpan(position, next));
            position += next;
            var entity = XmlEntities.Decode(bytes, position, XmlEntities.Mode.Html);
            output.Write(entity.Bytes.Span);
            position = entity.NextIndex;
        }
        return Encoding.UTF8.GetString(output.WrittenSpan);
    }
}
