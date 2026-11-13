using System.Text;
using System.Text.RegularExpressions;

namespace WinNewsWire.Core;

public static partial class StringExtensions
{
    private const string PlainTextDomainLabel = @"[\p{L}\p{N}](?:[\p{L}\p{N}\p{M}-]*[\p{L}\p{N}\p{M}])?";
    private const string PlainTextDomain = PlainTextDomainLabel + @"(?:\." + PlainTextDomainLabel
        + @")*\.(?:xn--[a-z0-9](?:[a-z0-9-]*[a-z0-9])?|[\p{L}]{2,})";
    private const string PlainTextEmailAtom = @"[a-z0-9!#$%&'*+/=?^_`{|}~-]+";
    private static readonly Regex PlainTextUrl = new(
        @"\b(?<explicit>(?:https?://|mailto:)[^\s<>""']+)"
        + @"|(?<email>" + PlainTextEmailAtom + @"(?:\." + PlainTextEmailAtom + ")*@" + PlainTextDomain + ")"
        + @"|(?<web>" + PlainTextDomain + @"(?::[0-9]{1,5})?(?:[/?#][^\s<>""']*)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static string ConvertingPlainTextToHtml(this string text)
    {
        var paragraphs = new List<string>();
        var lines = new List<string>();
        void EndParagraph()
        {
            if (lines.Count == 0) return;
            paragraphs.Add("<p>" + string.Join("<br>\n", lines) + "</p>");
            lines.Clear();
        }
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) EndParagraph();
            else lines.Add(LinkifiedPlainTextLine(line));
        }
        EndParagraph();
        return string.Join("\n", paragraphs);
    }

    private static string LinkifiedPlainTextLine(string line)
    {
        var html = new StringBuilder();
        int position = 0;
        foreach (Match match in PlainTextUrl.Matches(line))
        {
            string value = TrimPlainTextUrl(match.Value);
            bool isExplicit = match.Groups["explicit"].Success;
            if (!isExplicit && !IsBareAddressBoundary(line, match.Index, value.Length)) continue;
            string target = isExplicit ? value : (match.Groups["email"].Success ? "mailto:" : "http://") + value;
            if (!Uri.TryCreate(target, UriKind.Absolute, out var url)
                || url.Scheme is not ("http" or "https" or "mailto")
                || url.Scheme is "http" or "https" && url.Host.Length == 0) continue;
            if (!isExplicit && (url.Host.Length > 253 || url.Host.Split('.').Any(label => label.Length > 63))) continue;
            html.Append(line[position..match.Index].EscapingSpecialXmlCharacters());
            html.Append("<a href=\"").Append(url.AbsoluteUri.EscapingSpecialXmlCharacters()).Append("\">")
                .Append(value.EscapingSpecialXmlCharacters()).Append("</a>");
            position = match.Index + value.Length;
        }
        html.Append(line[position..].EscapingSpecialXmlCharacters());
        return html.ToString();
    }

    private static bool IsBareAddressBoundary(string line, int start, int length)
    {
        if (start > 0 && (char.IsLetterOrDigit(line[start - 1])
            || line[start - 1] is '_' or '@' or '.' or '/' or '\\' or '-' or ':')) return false;
        int end = start + length;
        if (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] is '_' or '@' or '-')) return false;
        return end + 1 >= line.Length || line[end] != '.' || !char.IsLetterOrDigit(line[end + 1]);
    }

    private static string TrimPlainTextUrl(string value)
    {
        int parentheses = 0, brackets = 0, braces = 0;
        foreach (char character in value)
        {
            switch (character)
            {
                case '(': parentheses--; break;
                case ')': parentheses++; break;
                case '[': brackets--; break;
                case ']': brackets++; break;
                case '{': braces--; break;
                case '}': braces++; break;
            }
        }
        int end = value.Length;
        while (end > 0)
        {
            char last = value[end - 1];
            if (last == ')' && parentheses > 0) parentheses--;
            else if (last == ']' && brackets > 0) brackets--;
            else if (last == '}' && braces > 0) braces--;
            else if (last is not ('.' or ',' or ';' or ':' or '!' or '?')) break;
            end--;
        }
        return value[..end];
    }
}
