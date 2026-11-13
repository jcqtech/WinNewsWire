using System.Text;
using WinNewsWire.Parsers.Xml;

namespace WinNewsWire.Parsers;

public sealed class HtmlAttributes
{
    private readonly IReadOnlyList<(ReadOnlyMemory<byte> Name, string Value)> _attributes;
    internal HtmlAttributes(IReadOnlyList<(ReadOnlyMemory<byte> Name, string Value)> attributes) => _attributes = attributes;
    public static HtmlAttributes Empty { get; } = new([]);
    public int Count => _attributes.Count;
    public bool IsEmpty => Count == 0;
    public string? this[string name]
    {
        get
        {
            int byteCount = Encoding.UTF8.GetByteCount(name);
            Span<byte> needle = byteCount <= 256 ? stackalloc byte[byteCount] : new byte[byteCount];
            Encoding.UTF8.GetBytes(name, needle);
            for (int index = 0; index < _attributes.Count; index++)
            {
                var attribute = _attributes[index];
                if (XmlAscii.EqualsCaseInsensitive(attribute.Name.Span, needle)) return attribute.Value;
            }
            return null;
        }
    }
    public Dictionary<string, string> Dictionary()
    {
        var result = new Dictionary<string, string>(_attributes.Count, StringComparer.Ordinal);
        foreach (var attribute in _attributes) result[Encoding.UTF8.GetString(attribute.Name.Span)] = attribute.Value;
        return result;
    }
}
