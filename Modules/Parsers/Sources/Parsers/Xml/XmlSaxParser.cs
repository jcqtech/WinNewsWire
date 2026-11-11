using System.Buffers;
using System.Text;

namespace WinNewsWire.Parsers.Xml;

public sealed class XmlSaxParser(IXmlSaxParserDelegate parserDelegate)
{
    private readonly record struct StackEntry(XmlNamespace Namespace, ReadOnlyMemory<byte> LocalName);
    private readonly List<StackEntry> _elementStack = [];
    private readonly ArrayBufferWriter<byte> _characters = new(1024);
    private XmlNamespaceContext _namespaceContext = new();
    private bool _storingCharacters;
    private bool _rawCaptureRequested;
    private bool _parsing;

    public IXmlSaxParserDelegate? Delegate { get; set; } = parserDelegate;
    public ReadOnlyMemory<byte>? CurrentCharacters
    {
        get
        {
            if (!_storingCharacters) return null;
            return _characters.WrittenSpan.ToArray();
        }
    }
    public string? CurrentStringWithTrimmedWhitespace
    {
        get
        {
            if (!_storingCharacters || _characters.WrittenCount == 0) return null;
            // Avoid a second large string for common XML padding; Trim still handles Unicode whitespace.
            var bytes = _characters.WrittenSpan;
            while (!bytes.IsEmpty && IsAsciiWhitespace(bytes[0])) bytes = bytes[1..];
            while (!bytes.IsEmpty && IsAsciiWhitespace(bytes[^1])) bytes = bytes[..^1];
            var text = Encoding.UTF8.GetString(bytes).Trim();
            return text.Length == 0 ? null : text;
        }
    }

    private static bool IsAsciiWhitespace(byte value) => value is 9 or 10 or 11 or 12 or 13 or 32;

    public void CaptureRawInnerContent() => _rawCaptureRequested = true;
    public void BeginStoringCharacters() { _storingCharacters = true; _characters.Clear(); }
    public void EndStoringCharacters() { _storingCharacters = false; _characters.Clear(); }

    public void Parse(byte[] bytes)
    {
        if (_parsing) throw new InvalidOperationException("XML parsing is not reentrant.");
        _parsing = true;
        try
        {
            var utf8 = XmlEncoding.ToUtf8(bytes);
            var scanner = new XmlScanner(utf8);
            while (true)
            {
                var next = scanner.Next();
                switch (next.Kind)
                {
                    case XmlScanner.EventKind.StartElement:
                        var innerStart = scanner.Position;
                        if (HandleStart(next) is { } capture)
                            RunPassThrough(innerStart, capture, scanner, utf8);
                        break;
                    case XmlScanner.EventKind.EndElement:
                        HandleEnd(next);
                        break;
                    case XmlScanner.EventKind.Characters:
                        if (next.Bytes.IsEmpty) break;
                        if (_storingCharacters)
                        {
                            next.Bytes.Span.CopyTo(_characters.GetSpan(next.Bytes.Length));
                            _characters.Advance(next.Bytes.Length);
                        }
                        Delegate?.DidFindCharacters(this, next.Bytes);
                        break;
                    case XmlScanner.EventKind.EndOfDocument:
                        while (_elementStack.Count > 0) PopAndEmitEnd();
                        Delegate?.DidEnd(this);
                        return;
                }
            }
        }
        finally
        {
            _parsing = false;
            _elementStack.Clear();
            _namespaceContext = new();
        }
    }

    private StackEntry? HandleStart(XmlScanner.Event start)
    {
        var rawAttributes = start.Attributes ?? Array.Empty<XmlScanner.RawAttribute>();
        _namespaceContext.PushScope();
        for (int index = 0; index < rawAttributes.Count; index++)
        {
            var raw = rawAttributes[index];
            if (!IsXmlnsAttribute(raw)) continue;
            var bindingPrefix = raw.PrefixSlice is null ? null : Encoding.UTF8.GetString(raw.LocalNameSlice.Span);
            _namespaceContext.AddBinding(bindingPrefix, Encoding.UTF8.GetString(raw.ValueSlice.Span));
        }
        var prefix = Decode(start.Prefix);
        var ns = new XmlNamespace(prefix, _namespaceContext.Resolve(prefix));
        List<XmlAttributes.Attribute>? visible = null;
        for (int index = 0; index < rawAttributes.Count; index++)
        {
            var raw = rawAttributes[index];
            if (IsXmlnsAttribute(raw)) continue;
            var attributePrefix = Decode(raw.PrefixSlice);
            var uri = attributePrefix is null ? null : _namespaceContext.Resolve(attributePrefix);
            (visible ??= []).Add(new(new(attributePrefix, uri), raw.LocalNameSlice, raw.ValueSlice));
        }
        var attributes = visible is null ? XmlAttributes.Empty : new XmlAttributes(visible);
        EndStoringCharacters();
        _rawCaptureRequested = false;
        Delegate?.DidStartElement(this, start.LocalName, ns, attributes);
        var capture = _rawCaptureRequested;
        _rawCaptureRequested = false;
        var entry = new StackEntry(ns, start.LocalName);
        if (capture && !start.SelfClosing) return entry;
        if (start.SelfClosing) EmitEnd(entry);
        else _elementStack.Add(entry);
        return null;
    }

    private void RunPassThrough(int innerStart, StackEntry outer, XmlScanner scanner, byte[] utf8)
    {
        var depth = 0;
        while (true)
        {
            var next = scanner.Next();
            switch (next.Kind)
            {
                case XmlScanner.EventKind.StartElement:
                    if (!next.SelfClosing && SameName(next.Prefix, next.LocalName, outer)) depth++;
                    break;
                case XmlScanner.EventKind.EndElement:
                    if (!SameName(next.Prefix, next.LocalName, outer)) break;
                    if (depth == 0)
                    {
                        EmitPassThroughEnd(utf8.AsMemory(innerStart, scanner.EventStart - innerStart), outer);
                        return;
                    }
                    depth--;
                    break;
                case XmlScanner.EventKind.EndOfDocument:
                    EmitPassThroughEnd(utf8.AsMemory(innerStart), outer);
                    return;
            }
        }
    }

    private void EmitPassThroughEnd(ReadOnlyMemory<byte> bytes, StackEntry outer)
    {
        Delegate?.DidCaptureRawInnerContent(this, bytes, outer.LocalName, outer.Namespace);
        EmitEnd(outer);
    }

    private void HandleEnd(XmlScanner.Event end)
    {
        var prefix = Decode(end.Prefix);
        var match = -1;
        for (var i = _elementStack.Count - 1; i >= 0; i--)
        {
            var entry = _elementStack[i];
            if (entry.Namespace.Prefix != prefix || !entry.LocalName.Span.SequenceEqual(end.LocalName.Span)) continue;
            match = i;
            break;
        }
        if (match >= 0)
        {
            while (_elementStack.Count > match) PopAndEmitEnd();
        }
        else
        {
            EndStoringCharacters();
            Delegate?.DidEndElement(this, end.LocalName, new(prefix, _namespaceContext.Resolve(prefix)));
        }
    }

    private void PopAndEmitEnd()
    {
        var entry = _elementStack[^1];
        _elementStack.RemoveAt(_elementStack.Count - 1);
        EmitEnd(entry);
    }

    private void EmitEnd(StackEntry entry)
    {
        Delegate?.DidEndElement(this, entry.LocalName, entry.Namespace);
        EndStoringCharacters();
        _namespaceContext.PopScope();
    }

    private static bool SameName(ReadOnlyMemory<byte>? prefix, ReadOnlyMemory<byte> local, StackEntry outer)
        => local.Span.SequenceEqual(outer.LocalName.Span) && Decode(prefix) == outer.Namespace.Prefix;

    private static string? Decode(ReadOnlyMemory<byte>? bytes)
        => bytes is { } value ? Encoding.UTF8.GetString(value.Span) : null;

    private static bool IsXmlnsAttribute(XmlScanner.RawAttribute attribute)
        => (attribute.PrefixSlice ?? attribute.LocalNameSlice).Span.SequenceEqual("xmlns"u8);
}
