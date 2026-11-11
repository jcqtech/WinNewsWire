using System.Globalization;
using System.Text;
using WinNewsWire.Parsers.Utilities;

namespace WinNewsWire.AppShared.Extensions;

[Flags]
public enum InlineTextStyle
{
    None = 0, Bold = 1, Italic = 2, Superscript = 4, Subscript = 8,
    Underline = 16, Strikethrough = 32, Monospace = 64
}

public readonly record struct AttributedTextRun(string Text, InlineTextStyle Style);

public sealed class AttributedText
{
    public static AttributedText Empty { get; } = new("", []);
    public string Text { get; }
    public IReadOnlyList<AttributedTextRun> Runs { get; }
    internal AttributedText(string text, IEnumerable<AttributedTextRun> runs)
    {
        Text = text;
        Runs = Array.AsReadOnly(runs.ToArray());
    }
}

public static class SimpleHtml
{
    public static AttributedText Parse(string html, CultureInfo? culture = null)
    {
        if (html.Length == 0) return AttributedText.Empty;
        var text = new StringBuilder();
        var tag = new StringBuilder();
        var styles = new Dictionary<InlineTextStyle, int>();
        var ranges = new List<(int Start, int Length, InlineTextStyle Style)>();
        var inTag = false;
        var closing = false;
        var quoteDepth = 0;
        var quotes = QuotationMarks(culture ?? CultureInfo.CurrentCulture);

        for (var i = 0; i < html.Length; i++)
        {
            var character = html[i];
            if (character == '<' && !inTag)
            {
                tag.Clear();
                if (++i == html.Length) break;
                closing = html[i] == '/';
                if (!closing) tag.Append(html[i]);
                inTag = true;
            }
            else if (character == '>' && inTag)
            {
                var start = ranges.Count == 0 ? 0 : ranges[^1].Start + ranges[^1].Length;
                var style = styles.Keys.Aggregate(InlineTextStyle.None, (combined, value) => combined | value);
                ranges.Add((start, text.Length - start, style));
                var name = tag.ToString();
                if (name == "q")
                {
                    if (!closing) quoteDepth++;
                    var alternate = quoteDepth % 2 != 1;
                    text.Append(closing ? alternate ? quotes.AlternateEnd : quotes.End
                        : alternate ? quotes.AlternateBegin : quotes.Begin);
                    if (closing) quoteDepth--;
                }
                var addedStyle = StyleForTag(name);
                if (addedStyle != InlineTextStyle.None)
                {
                    var count = styles.GetValueOrDefault(addedStyle);
                    if (!closing) styles[addedStyle] = count + 1;
                    else if (count <= 1) styles.Remove(addedStyle);
                    else styles[addedStyle] = count - 1;
                }
                inTag = false;
            }
            else if (inTag) tag.Append(character);
            else if (character == '&')
            {
                var entity = new StringBuilder("&");
                char? whitespace = null;
                while (++i < html.Length)
                {
                    var next = html[i];
                    if (char.IsWhiteSpace(next)) { whitespace = next; break; }
                    entity.Append(next);
                    if (next == ';') break;
                }
                text.Append(entity.ToString().DecodeHtmlEntities());
                if (whitespace is { } space) text.Append(space);
            }
            else text.Append(character);
        }

        var result = text.ToString();
        var runs = new List<AttributedTextRun>();
        foreach (var range in ranges)
            AddRun(result.Substring(range.Start, range.Length), range.Style);
        var end = ranges.Count == 0 ? 0 : ranges[^1].Start + ranges[^1].Length;
        AddRun(result[end..], InlineTextStyle.None);
        return new AttributedText(result, runs);

        void AddRun(string value, InlineTextStyle style)
        {
            if (value.Length == 0) return;
            if (runs.Count > 0 && runs[^1].Style == style) runs[^1] = new(runs[^1].Text + value, style);
            else runs.Add(new(value, style));
        }
    }

    private static InlineTextStyle StyleForTag(string tag) => tag switch
    {
        "b" or "strong" => InlineTextStyle.Bold,
        "i" or "em" or "cite" or "var" or "dfn" => InlineTextStyle.Italic,
        "sup" => InlineTextStyle.Superscript,
        "sub" => InlineTextStyle.Subscript,
        "u" or "ins" => InlineTextStyle.Underline,
        "s" or "del" => InlineTextStyle.Strikethrough,
        "code" or "samp" or "tt" or "kbd" => InlineTextStyle.Monospace,
        _ => InlineTextStyle.None
    };

    private static (string Begin, string End, string AlternateBegin, string AlternateEnd) QuotationMarks(CultureInfo culture)
        => culture.TwoLetterISOLanguageName switch
        {
            "de" => ("\u201E", "\u201C", "\u201A", "\u2018"),
            "fr" => ("\u00AB", "\u00BB", "\u201C", "\u201D"),
            "ja" => ("\u300C", "\u300D", "\u300E", "\u300F"),
            "ru" => ("\u00AB", "\u00BB", "\u201E", "\u201C"),
            "zh" when culture.Name.Contains("TW", StringComparison.OrdinalIgnoreCase)
                || culture.Name.Contains("HK", StringComparison.OrdinalIgnoreCase)
                || culture.Name.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                => ("\u300C", "\u300D", "\u300E", "\u300F"),
            _ => ("\u201C", "\u201D", "\u2018", "\u2019")
        };
}
