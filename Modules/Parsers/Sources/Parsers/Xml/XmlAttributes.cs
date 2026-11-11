using System.Text;

namespace WinNewsWire.Parsers.Xml;

public sealed class XmlAttributes
{
    internal readonly record struct Attribute(XmlNamespace Namespace, ReadOnlyMemory<byte> LocalNameSlice, ReadOnlyMemory<byte> ValueSlice)
    {
        public string LocalName => Encoding.UTF8.GetString(LocalNameSlice.Span);
        public string Value => Encoding.UTF8.GetString(ValueSlice.Span);
        public string QualifiedName => Namespace.Prefix is { } prefix ? $"{prefix}:{LocalName}" : LocalName;
    }

    private readonly IReadOnlyList<Attribute> _attributes;

    internal XmlAttributes(IReadOnlyList<Attribute> attributes) => _attributes = attributes;

    public static XmlAttributes Empty { get; } = new([]);
    public int Count => _attributes.Count;
    public bool IsEmpty => Count == 0;
    public string? this[string name] => ValueForName(name, false);
    public string? ValueForNameCaseInsensitive(string name) => ValueForName(name, true);

    public Dictionary<string, string> Dictionary()
    {
        var result = new Dictionary<string, string>(_attributes.Count, StringComparer.Ordinal);
        foreach (var attribute in _attributes)
            result[attribute.QualifiedName] = attribute.Value;
        return result;
    }

    public void ForEach(Action<XmlNamespace, string, string> action)
    {
        foreach (var attribute in _attributes)
            action(attribute.Namespace, attribute.LocalName, attribute.Value);
    }

    private string? ValueForName(string name, bool caseInsensitive)
    {
        var needle = Encoding.UTF8.GetBytes(name).AsSpan();
        var colon = needle.IndexOf(XmlAscii.Colon);
        foreach (var attribute in _attributes)
        {
            if (colon < 0)
            {
                if (attribute.Namespace.Prefix is null && Equal(attribute.LocalNameSlice.Span, needle, caseInsensitive))
                    return attribute.Value;
            }
            else if (attribute.Namespace.Prefix is { } prefix &&
                     Equal(Encoding.UTF8.GetBytes(prefix), needle[..colon], caseInsensitive) &&
                     Equal(attribute.LocalNameSlice.Span, needle[(colon + 1)..], caseInsensitive))
                return attribute.Value;
        }
        return null;
    }

    private static bool Equal(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, bool caseInsensitive)
        => caseInsensitive ? XmlAscii.EqualsCaseInsensitive(a, b) : a.SequenceEqual(b);
}
