using System.Collections.Concurrent;
using WinNewsWire.Core;

namespace WinNewsWire.Articles;

/// <summary>Port of <c>databaseIDWithString</c> — MD5 hex of input, cached.</summary>
public static class DatabaseID
{
    private static readonly ConcurrentDictionary<string, string> _cache = new();
    public static string For(string s) => _cache.GetOrAdd(s, HashExtensions.Md5Hex);
}
