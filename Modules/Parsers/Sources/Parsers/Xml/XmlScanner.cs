using System.Buffers;

namespace WinNewsWire.Parsers.Xml;

internal sealed class XmlScanner(byte[] input)
{
    internal enum EventKind { StartElement, EndElement, Characters, EndOfDocument }
    internal readonly record struct Event(EventKind Kind, ReadOnlyMemory<byte>? Prefix = null,
        ReadOnlyMemory<byte> LocalName = default, IReadOnlyList<RawAttribute>? Attributes = null,
        bool SelfClosing = false, ReadOnlyMemory<byte> Bytes = default);
    internal readonly record struct RawAttribute(ReadOnlyMemory<byte>? PrefixSlice,
        ReadOnlyMemory<byte> LocalNameSlice, ReadOnlyMemory<byte> ValueSlice);

    private static readonly Event End = new(EventKind.EndOfDocument);
    private int _position;
    public int Position => _position;
    public int EventStart { get; private set; }

    public Event Next()
    {
        while (_position < input.Length)
        {
            EventStart = _position;
            if (input[_position] == XmlAscii.LessThan)
            {
                if (ScanMarkup() is { } markup) return markup;
                continue;
            }
            return new(EventKind.Characters, Bytes: ScanValue(null, false));
        }
        EventStart = _position;
        return End;
    }

    private Event? ScanMarkup()
    {
        var next = Peek(1);
        if (next == XmlAscii.Exclamation)
        {
            if (MatchPrefix("<!--"u8)) { ConsumeUntil("-->"u8, 4); return null; }
            if (MatchPrefix("<![CDATA["u8)) return ScanCData();
            if (MatchPrefix("<!DOCTYPE"u8) || MatchPrefix("<!doctype"u8)) { ConsumeDocType(); return null; }
            ConsumeThroughGreaterThan();
            return null;
        }
        if (next == XmlAscii.Question) { ConsumeUntil("?>"u8, 2); return null; }
        if (next == XmlAscii.Slash) return ScanEndTag();
        if (next?.IsXmlNameStart() == true) return ScanStartTag();
        return new(EventKind.Characters, Bytes: input.AsMemory(_position++, 1));
    }

    private Event ScanStartTag()
    {
        _position++;
        var (prefix, local) = ScanQualifiedName();
        List<RawAttribute>? attributes = null;
        var selfClosing = false;
        while (_position < input.Length)
        {
            SkipWhitespace();
            if (_position >= input.Length) break;
            var b = input[_position];
            if (b == XmlAscii.GreaterThan) { _position++; break; }
            if (b == XmlAscii.Slash)
            {
                if (Peek(1) == XmlAscii.GreaterThan) { selfClosing = true; _position += 2; break; }
                _position++;
                continue;
            }
            if (!b.IsXmlNameStart()) { _position++; continue; }
            (attributes ??= []).Add(ScanAttribute());
        }
        return new(EventKind.StartElement, prefix, local, attributes, selfClosing);
    }

    private RawAttribute ScanAttribute()
    {
        var (prefix, local) = ScanQualifiedName();
        SkipWhitespace();
        if (_position >= input.Length || input[_position] != XmlAscii.EqualsSign)
            return new(prefix, local, ReadOnlyMemory<byte>.Empty);
        _position++;
        SkipWhitespace();
        if (_position >= input.Length) return new(prefix, local, ReadOnlyMemory<byte>.Empty);

        byte? quote = null;
        if (input[_position] is XmlAscii.DoubleQuote or XmlAscii.SingleQuote) quote = input[_position++];
        return new(prefix, local, ScanValue(quote, true));
    }

    private ReadOnlyMemory<byte> ScanValue(byte? quote, bool attribute)
    {
        var start = _position;
        var copiedThrough = start;
        ArrayBufferWriter<byte>? output = null;
        while (_position < input.Length)
        {
            var b = input[_position];
            var atEnd = quote is { } q ? b == q
                : attribute ? b.IsAsciiWhitespace() || b is XmlAscii.GreaterThan or XmlAscii.Slash
                : b == XmlAscii.LessThan;
            if (atEnd) break;
            if (b == XmlAscii.Ampersand)
            {
                output ??= new ArrayBufferWriter<byte>();
                Append(output, input.AsSpan(copiedThrough, _position - copiedThrough));
                var entity = XmlEntities.Decode(input, _position, XmlEntities.Mode.XmlStrict);
                Append(output, entity.Bytes.Span);
                _position = entity.NextIndex;
                copiedThrough = _position;
            }
            else _position++;
        }
        ReadOnlyMemory<byte> value;
        if (output is null) value = input.AsMemory(start, _position - start);
        else
        {
            Append(output, input.AsSpan(copiedThrough, _position - copiedThrough));
            value = output.WrittenMemory;
        }
        if (quote is not null && _position < input.Length) _position++;
        return value;
    }

    private static void Append(ArrayBufferWriter<byte> output, ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(output.GetSpan(bytes.Length));
        output.Advance(bytes.Length);
    }

    private Event ScanEndTag()
    {
        _position += 2;
        var (prefix, local) = ScanQualifiedName();
        ConsumeThroughGreaterThan();
        return new(EventKind.EndElement, prefix, local);
    }

    private Event ScanCData()
    {
        _position += 9;
        var start = _position;
        var offset = input.AsSpan(start).IndexOf("]]>"u8);
        if (offset < 0)
        {
            _position = input.Length;
            return new(EventKind.Characters, Bytes: input.AsMemory(start));
        }
        _position += offset + 3;
        return new(EventKind.Characters, Bytes: input.AsMemory(start, offset));
    }

    private void ConsumeUntil(ReadOnlySpan<byte> terminator, int openingLength)
    {
        _position += openingLength;
        var offset = input.AsSpan(_position).IndexOf(terminator);
        _position = offset < 0 ? input.Length : _position + offset + terminator.Length;
    }

    private void ConsumeDocType()
    {
        _position += 9;
        var depth = 0;
        while (_position < input.Length)
        {
            var b = input[_position++];
            if (b == XmlAscii.LeftBracket) depth++;
            else if (b == XmlAscii.RightBracket && depth > 0) depth--;
            else if (b == XmlAscii.GreaterThan && depth == 0) return;
        }
    }

    private void ConsumeThroughGreaterThan()
    {
        while (_position < input.Length)
            if (input[_position++] == XmlAscii.GreaterThan) return;
    }

    private (ReadOnlyMemory<byte>? Prefix, ReadOnlyMemory<byte> LocalName) ScanQualifiedName()
    {
        var start = _position;
        while (_position < input.Length && input[_position].IsXmlNameChar()) _position++;
        var name = input.AsMemory(start, _position - start);
        var colon = name.Span.IndexOf(XmlAscii.Colon);
        if (colon < 0) return (null, name);
        return (name[..colon], name[(colon + 1)..]);
    }

    private byte? Peek(int offset) => _position + offset < input.Length ? input[_position + offset] : null;
    private void SkipWhitespace()
    {
        while (_position < input.Length && input[_position].IsAsciiWhitespace()) _position++;
    }
    private bool MatchPrefix(ReadOnlySpan<byte> literal) => input.AsSpan(_position).StartsWith(literal);
}
