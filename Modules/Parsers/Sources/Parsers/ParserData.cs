namespace WinNewsWire.Parsers;

/// <summary>
/// Mirrors <c>ParserData</c> in NetNewsWire's RSParser Objective-C module. Pairs the
/// raw bytes of a feed/HTML payload with the URL they came from (used by parsers when
/// a feed does not specify its own URL).
/// </summary>
public sealed record ParserData(string Url, byte[] Data);
