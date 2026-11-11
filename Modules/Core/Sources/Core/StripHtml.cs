using System.Text;

namespace WinNewsWire.Core;

public static partial class StringExtensions
{
    private static readonly string[] BlockTags =
        ["p>", "/p>", "div>", "/div>", "blockquote>", "/blockquote>", "br>", "br/>", "br />", "/li>"];

    public static string StripHtml(this string s) => StripHtml(s, maxCharacters: null);

    /// <summary>Port of RSCore's StripHTML state machine. A nonpositive limit is unlimited.</summary>
    public static string StripHtml(this string s, int? maxCharacters)
    {
        var output = new StringBuilder(s.Length);
        int tagLevel = 0, charactersAdded = 0;
        bool inScript = false, inStyle = false, lastCharacterWasSpace = true;
        char quote = '\0';

        for (int i = 0; i < s.Length; i++)
        {
            if (maxCharacters > 0 && charactersAdded >= maxCharacters) break;
            char c = s[i];

            if (c == '<')
            {
                if (quote != '\0') continue;
                if (!inScript && !inStyle) tagLevel++;

                var tag = s.AsSpan(i + 1);
                if (tag.StartsWith("script", StringComparison.OrdinalIgnoreCase)) inScript = true;
                else if (tag.StartsWith("style", StringComparison.OrdinalIgnoreCase)) inStyle = true;
                else if (tag.StartsWith("/script", StringComparison.OrdinalIgnoreCase)) inScript = false;
                else if (tag.StartsWith("/style", StringComparison.OrdinalIgnoreCase)) inStyle = false;

                foreach (var blockTag in BlockTags)
                {
                    if (!tag.StartsWith(blockTag, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!lastCharacterWasSpace)
                    {
                        output.Append(' ');
                        lastCharacterWasSpace = true;
                        charactersAdded++;
                    }
                    break;
                }
                continue;
            }

            if (c == '>')
            {
                if (quote == '\0' && !inScript && !inStyle && tagLevel > 0) tagLevel--;
                continue;
            }

            if (tagLevel > 0 && !inScript && !inStyle && c is '"' or '\'')
            {
                if (quote == c) quote = '\0';
                else if (quote == '\0') quote = c;
                continue;
            }

            if (tagLevel > 0 || inScript || inStyle) continue;

            if (c is ' ' or '\t' or '\r' or '\n')
            {
                if (!lastCharacterWasSpace)
                {
                    output.Append(' ');
                    lastCharacterWasSpace = true;
                    charactersAdded++;
                }
                continue;
            }

            lastCharacterWasSpace = false;
            output.Append(c);
            // Swift counts UTF-8 code points, not UTF-16 code units.
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                output.Append(s[++i]);
            charactersAdded++;
        }

        return output.ToString().TrimEnd(' ');
    }
}
