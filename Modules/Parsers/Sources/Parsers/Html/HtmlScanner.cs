using System.Buffers;
using System.Text;
using WinNewsWire.Parsers.Xml;

namespace WinNewsWire.Parsers;

public sealed class HtmlScanner(IHtmlScannerDelegate scannerDelegate)
{
    public IHtmlScannerDelegate? Delegate { get; set; } = scannerDelegate;
    private byte[] _input = [];
    private int _position;
    private bool _parsing;
    private bool _stopped;

    public void StopParsing() => _stopped = true;

    public void Parse(byte[] bytes)
    {
        if (_parsing) throw new InvalidOperationException("HTML scanning is not reentrant.");
        _parsing = true;
        _input = bytes;
        _position = 0;
        _stopped = false;
        try
        {
            while (!_stopped && _position < _input.Length)
            {
                if (_input[_position] == XmlAscii.LessThan) ScanMarkup();
                else
                {
                    var characters = ScanValue(null, false);
                    Delegate?.DidFindCharacters(this, characters);
                }
            }
            Delegate?.DidEnd(this);
        }
        finally
        {
            _parsing = false;
            _input = [];
        }
    }

    private void ScanMarkup()
    {
        var next = Peek(1);
        if (next == XmlAscii.Exclamation)
        {
            if (Matches("<!--"u8)) ConsumeUntil("-->"u8, 4);
            else if (Matches("<![CDATA["u8)) ConsumeCData();
            else ConsumeThroughGreaterThan();
        }
        else if (next == XmlAscii.Question) ConsumeUntil("?>"u8, 2);
        else if (next == XmlAscii.Slash) ScanEndTag();
        else if (next?.IsXmlNameStart() == true) ScanStartTag();
        else
        {
            var bytes = _input.AsMemory(_position++, 1);
            Delegate?.DidFindCharacters(this, bytes);
        }
    }

    private void ScanStartTag()
    {
        _position++;
        var name = ScanTagName();
        var attributes = new List<(ReadOnlyMemory<byte> Name, string Value)>();
        var selfClosing = false;
        while (_position < _input.Length)
        {
            SkipWhitespace();
            if (_position >= _input.Length) break;
            var b = _input[_position];
            if (b == XmlAscii.GreaterThan) { _position++; break; }
            if (b == XmlAscii.Slash)
            {
                if (Peek(1) == XmlAscii.GreaterThan) { selfClosing = true; _position += 2; break; }
                _position++;
            }
            else if (b.IsXmlNameStart()) attributes.Add(ScanAttribute());
            else _position++;
        }
        Delegate?.DidStartTag(this, name, attributes.Count == 0 ? HtmlAttributes.Empty : new(attributes), selfClosing);
        if (!selfClosing && IsRawTextTag(name.Span))
            ConsumeRawText(name.Span);
    }

    private static bool IsRawTextTag(ReadOnlySpan<byte> name)
        => XmlAscii.EqualsCaseInsensitive(name, "script"u8) || XmlAscii.EqualsCaseInsensitive(name, "style"u8)
            || XmlAscii.EqualsCaseInsensitive(name, "textarea"u8) || XmlAscii.EqualsCaseInsensitive(name, "title"u8)
            || XmlAscii.EqualsCaseInsensitive(name, "xmp"u8) || XmlAscii.EqualsCaseInsensitive(name, "noembed"u8);

    private (ReadOnlyMemory<byte> Name, string Value) ScanAttribute()
    {
        var start = _position;
        while (_position < _input.Length && !_input[_position].IsAsciiWhitespace()
            && _input[_position] is not (XmlAscii.EqualsSign or XmlAscii.GreaterThan or XmlAscii.Slash)) _position++;
        ReadOnlyMemory<byte> name = _input.AsMemory(start, _position - start);
        SkipWhitespace();
        if (_position >= _input.Length || _input[_position] != XmlAscii.EqualsSign) return (name, "");
        _position++;
        SkipWhitespace();
        if (_position >= _input.Length) return (name, "");
        byte? quote = null;
        if (_input[_position] is XmlAscii.DoubleQuote or XmlAscii.SingleQuote) quote = _input[_position++];
        return (name, Encoding.UTF8.GetString(ScanValue(quote, true).Span));
    }

    private ReadOnlyMemory<byte> ScanValue(byte? quote, bool attribute)
    {
        var start = _position;
        var copiedThrough = start;
        ArrayBufferWriter<byte>? output = null;
        while (_position < _input.Length)
        {
            var b = _input[_position];
            var atEnd = quote is { } q ? b == q
                : attribute ? b.IsAsciiWhitespace() || b == XmlAscii.GreaterThan
                : b == XmlAscii.LessThan;
            if (atEnd) break;
            if (b == XmlAscii.Ampersand)
            {
                output ??= new ArrayBufferWriter<byte>();
                output.Write(_input.AsSpan(copiedThrough, _position - copiedThrough));
                var entity = XmlEntities.Decode(_input, _position, XmlEntities.Mode.Html);
                output.Write(entity.Bytes.Span);
                _position = entity.NextIndex;
                copiedThrough = _position;
            }
            else _position++;
        }
        ReadOnlyMemory<byte> result;
        if (output is null) result = _input.AsMemory(start, _position - start);
        else
        {
            output.Write(_input.AsSpan(copiedThrough, _position - copiedThrough));
            result = output.WrittenMemory;
        }
        if (quote is not null && _position < _input.Length) _position++;
        return result;
    }

    private void ScanEndTag()
    {
        _position += 2;
        var name = ScanTagName();
        ConsumeThroughGreaterThan();
        Delegate?.DidEndTag(this, name);
    }

    private void ConsumeRawText(ReadOnlySpan<byte> name)
    {
        var start = _position;
        bool decodeEntities = XmlAscii.EqualsCaseInsensitive(name, "textarea"u8) || XmlAscii.EqualsCaseInsensitive(name, "title"u8);
        while (_position < _input.Length)
        {
            if (_input[_position] == XmlAscii.LessThan && Peek(1) == XmlAscii.Slash)
            {
                var closerStart = _position + 2;
                var nameEnd = closerStart;
                while (nameEnd < _input.Length && _input[nameEnd].IsXmlNameChar()) nameEnd++;
                var candidate = _input.AsMemory(closerStart, nameEnd - closerStart);
                if (XmlAscii.EqualsCaseInsensitive(candidate.Span, name))
                {
                    EmitRawText(start, _position - start, decodeEntities);
                    _position = nameEnd;
                    ConsumeThroughGreaterThan();
                    Delegate?.DidEndTag(this, candidate);
                    return;
                }
            }
            _position++;
        }
        EmitRawText(start, _position - start, decodeEntities);
    }

    private void EmitRawText(int start, int length, bool decodeEntities)
    {
        if (length == 0) return;
        var bytes = _input.AsMemory(start, length);
        if (!decodeEntities || !bytes.Span.Contains(XmlAscii.Ampersand))
        {
            Delegate?.DidFindCharacters(this, bytes);
            return;
        }
        var output = new ArrayBufferWriter<byte>(length);
        int end = start + length;
        for (int index = start; index < end;)
        {
            if (_input[index] == XmlAscii.Ampersand)
            {
                var entity = XmlEntities.Decode(_input, index, XmlEntities.Mode.Html);
                output.Write(entity.Bytes.Span);
                index = Math.Max(entity.NextIndex, index + 1);
            }
            else output.Write(_input.AsSpan(index++, 1));
        }
        Delegate?.DidFindCharacters(this, output.WrittenMemory);
    }

    private void ConsumeCData()
    {
        _position += 9;
        var start = _position;
        var offset = _input.AsSpan(start).IndexOf("]]>"u8);
        var length = offset < 0 ? _input.Length - start : offset;
        _position = offset < 0 ? _input.Length : start + offset + 3;
        if (length > 0) Delegate?.DidFindCharacters(this, _input.AsMemory(start, length));
    }
    private void ConsumeUntil(ReadOnlySpan<byte> terminator, int openingLength)
    {
        _position += openingLength;
        var offset = _input.AsSpan(_position).IndexOf(terminator);
        _position = offset < 0 ? _input.Length : _position + offset + terminator.Length;
    }
    private void ConsumeThroughGreaterThan()
    {
        while (_position < _input.Length)
            if (_input[_position++] == XmlAscii.GreaterThan) return;
    }
    private ReadOnlyMemory<byte> ScanTagName()
    {
        var start = _position;
        while (_position < _input.Length && _input[_position].IsXmlNameChar()) _position++;
        return _input.AsMemory(start, _position - start);
    }
    private byte? Peek(int offset) => _position + offset < _input.Length ? _input[_position + offset] : null;
    private bool Matches(ReadOnlySpan<byte> literal) => _input.AsSpan(_position).StartsWith(literal);
    private void SkipWhitespace()
    {
        while (_position < _input.Length && _input[_position].IsAsciiWhitespace()) _position++;
    }
}

public interface IHtmlScannerDelegate
{
    void DidStartTag(HtmlScanner scanner, ReadOnlyMemory<byte> name, HtmlAttributes attributes, bool selfClosing) { }
    void DidEndTag(HtmlScanner scanner, ReadOnlyMemory<byte> name) { }
    void DidFindCharacters(HtmlScanner scanner, ReadOnlyMemory<byte> bytes) { }
    void DidEnd(HtmlScanner scanner) { }
}
