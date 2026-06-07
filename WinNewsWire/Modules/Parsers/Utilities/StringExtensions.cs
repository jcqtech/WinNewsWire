using System.Net;
using System.Text;

namespace WinNewsWire.Parsers.Utilities;

/// <summary>Port of <c>NSString (RSParser)</c> helpers used by the feed parsers.</summary>
internal static class StringExtensions
{
    public static string? NilIfEmptyOrWhitespace(this string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>Fast HTML entity decoder. Uses <see cref="WebUtility.HtmlDecode"/>
    /// which handles the full standard named + numeric entity set.</summary>
    public static string DecodeHtmlEntities(this string s) => WebUtility.HtmlDecode(s);
}
