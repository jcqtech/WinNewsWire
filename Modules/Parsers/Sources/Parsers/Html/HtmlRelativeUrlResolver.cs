using System.Text;

namespace WinNewsWire.Parsers;

/// <summary>Port of HTMLRelativeURLResolver: only rewritten attribute-value ranges change.</summary>
public static class HtmlRelativeUrlResolver
{
    public static string Resolve(string html, Uri baseUrl)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(baseUrl);
        if (!baseUrl.IsAbsoluteUri) throw new ArgumentException("An absolute base URL is required.", nameof(baseUrl));
        return new Scanner(html, baseUrl).Rewrite();
    }

    public static string Resolve(ReadOnlySpan<byte> utf8, Uri baseUrl) => Resolve(Encoding.UTF8.GetString(utf8), baseUrl);

    private sealed class Scanner(string html, Uri baseUrl)
    {
        private int _position;
        private readonly List<(int Start, int End, string Value)> _replacements = [];

        public string Rewrite()
        {
            while (_position < html.Length)
            {
                if (html[_position] != '<') { _position++; continue; }
                if (_position + 1 == html.Length) break;
                var next = html[_position + 1];
                if (next == '!')
                {
                    if (Matches("<!--")) SkipPast("-->");
                    else if (Matches("<![CDATA[")) SkipPast("]]>");
                    else SkipPast(">");
                }
                else if (next == '?') SkipPast("?>");
                else if (next == '/') SkipPast(">");
                else if (char.IsAsciiLetter(next)) ScanTag();
                else _position++;
            }
            if (_replacements.Count == 0) return html;
            var result = new StringBuilder(html.Length);
            int copied = 0;
            foreach (var replacement in _replacements)
            {
                result.Append(html, copied, replacement.Start - copied);
                result.Append(replacement.Value);
                copied = replacement.End;
            }
            return result.Append(html, copied, html.Length - copied).ToString();
        }

        private void ScanTag()
        {
            int start = ++_position;
            while (_position < html.Length && IsNameCharacter(html[_position])) _position++;
            var name = html[start.._position];
            bool selfClosing = false;
            while (_position < html.Length)
            {
                SkipWhitespace();
                if (_position == html.Length) return;
                char c = html[_position];
                if (c == '>') { _position++; break; }
                if (c == '/')
                {
                    if (_position + 1 < html.Length && html[_position + 1] == '>')
                    {
                        selfClosing = true;
                        _position += 2;
                        break;
                    }
                    _position++;
                }
                else if (IsNameStart(c)) ScanAttribute();
                else _position++;
            }
            if (!selfClosing && (name.Equals("script", StringComparison.OrdinalIgnoreCase)
                || name.Equals("style", StringComparison.OrdinalIgnoreCase)))
                SkipRawText(name);
        }

        private void ScanAttribute()
        {
            int start = _position;
            while (_position < html.Length && !IsWhitespace(html[_position]) && html[_position] is not ('=' or '>' or '/'))
                _position++;
            var name = html[start.._position];
            SkipWhitespace();
            if (_position == html.Length || html[_position] != '=') return;
            _position++;
            SkipWhitespace();
            if (_position == html.Length) return;
            int valueStart, valueEnd;
            char quote = html[_position];
            if (quote is '"' or '\'')
            {
                valueStart = ++_position;
                valueEnd = html.IndexOf(quote, valueStart);
                if (valueEnd < 0) { _position = html.Length; return; }
                _position = valueEnd + 1;
            }
            else
            {
                valueStart = _position;
                while (_position < html.Length && !IsWhitespace(html[_position]) && html[_position] != '>') _position++;
                valueEnd = _position;
            }
            if (name.Equals("srcset", StringComparison.OrdinalIgnoreCase)) ScanSrcset(valueStart, valueEnd);
            else if (name.Equals("href", StringComparison.OrdinalIgnoreCase)
                || name.Equals("src", StringComparison.OrdinalIgnoreCase)
                || name.Equals("poster", StringComparison.OrdinalIgnoreCase))
                AddReplacement(valueStart, valueEnd);
        }

        private void ScanSrcset(int start, int end)
        {
            int i = start;
            while (i < end)
            {
                while (i < end && (IsWhitespace(html[i]) || html[i] == ',')) i++;
                if (i == end) return;
                int urlStart = i;
                while (i < end && !IsWhitespace(html[i])) i++;
                int urlEnd = i;
                while (urlEnd > urlStart && html[urlEnd - 1] == ',') urlEnd--;
                if (urlEnd > urlStart) AddReplacement(urlStart, urlEnd);
                if (urlEnd == i)
                    while (i < end && html[i] != ',') i++;
            }
        }

        private void AddReplacement(int start, int end)
        {
            while (start < end && IsWhitespace(html[start])) start++;
            while (end > start && IsWhitespace(html[end - 1])) end--;
            if (start == end || html[start] == '#') return;
            var value = html[start..end];
            if (HasScheme(value)) return;
            if (Uri.TryCreate(baseUrl, value, out var resolved) && resolved.AbsoluteUri != value)
                _replacements.Add((start, end, resolved.AbsoluteUri));
        }

        private static bool HasScheme(string value)
        {
            if (!char.IsAsciiLetter(value[0])) return false;
            for (int i = 1; i < value.Length; i++)
            {
                char c = value[i];
                if (c == ':') return true;
                if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.')) return false;
            }
            return false;
        }

        private void SkipRawText(string name)
        {
            while (_position < html.Length)
            {
                int start = html.IndexOf("</", _position, StringComparison.Ordinal);
                if (start < 0) { _position = html.Length; return; }
                int end = start + 2 + name.Length;
                if (end <= html.Length && html.AsSpan(start + 2, name.Length).Equals(name, StringComparison.OrdinalIgnoreCase)
                    && (end == html.Length || html[end] is '>' or '/' || IsWhitespace(html[end])))
                {
                    _position = end;
                    SkipPast(">");
                    return;
                }
                _position = start + 2;
            }
        }

        private bool Matches(string value) => html.AsSpan(_position).StartsWith(value, StringComparison.Ordinal);
        private void SkipPast(string value)
        {
            int found = html.IndexOf(value, _position, StringComparison.Ordinal);
            _position = found < 0 ? html.Length : found + value.Length;
        }
        private void SkipWhitespace() { while (_position < html.Length && IsWhitespace(html[_position])) _position++; }
        private static bool IsWhitespace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f' or '\v';
        private static bool IsNameStart(char c) => char.IsAsciiLetter(c) || c is '_' or ':' || c >= 0x80;
        private static bool IsNameCharacter(char c) => IsNameStart(c) || char.IsAsciiDigit(c) || c is '-' or '.';
    }
}
