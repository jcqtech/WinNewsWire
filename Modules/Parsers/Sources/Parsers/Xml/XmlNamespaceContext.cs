namespace WinNewsWire.Parsers.Xml;

internal sealed class XmlNamespaceContext
{
    private readonly List<(string? Prefix, string Uri)> _bindings = [];
    private readonly Stack<int> _scopeStarts = [];

    public void PushScope(IEnumerable<(string? Prefix, string Uri)> bindings)
    {
        PushScope();
        _bindings.AddRange(bindings);
    }

    public void PushScope() => _scopeStarts.Push(_bindings.Count);
    public void AddBinding(string? prefix, string uri) => _bindings.Add((prefix, uri));

    public void PopScope()
    {
        if (_scopeStarts.TryPop(out var start))
            _bindings.RemoveRange(start, _bindings.Count - start);
    }

    public string? Resolve(string? prefix)
    {
        if (prefix == "xml") return "http://www.w3.org/XML/1998/namespace";
        if (prefix == "xmlns") return "http://www.w3.org/2000/xmlns/";
        for (var i = _bindings.Count - 1; i >= 0; i--)
            if (_bindings[i].Prefix == prefix) return _bindings[i].Uri;
        return null;
    }
}
