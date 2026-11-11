namespace WinNewsWire.Parsers;

public enum HtmlTagType { Link, Meta }
public sealed record HtmlTag(HtmlTagType Type, IReadOnlyDictionary<string, string> Attributes);
