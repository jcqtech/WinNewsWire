using System;
using System.Security.Cryptography;
using System.Text;

namespace WinNewsWire.Data;

/// <summary>
/// Generates deterministic IDs matching NetNewsWire's algorithm:
/// MD5(input.UTF8).ToLowerHexString (32-char lowercase hex).
/// </summary>
public static class DatabaseId
{
    /// <summary>
    /// Generates an article ID for local (non-synced) feeds.
    /// Formula: MD5(feedId + " " + uniqueId)
    /// </summary>
    public static string GenerateArticleId(string feedId, string uniqueId)
    {
        return HashString(feedId + " " + uniqueId);
    }

    /// <summary>
    /// Generates an author ID.
    /// Formula: MD5(name + url + avatarUrl + emailAddress) with nulls as empty strings.
    /// </summary>
    public static string GenerateAuthorId(string? name, string? url, string? avatarUrl, string? emailAddress)
    {
        return HashString((name ?? "") + (url ?? "") + (avatarUrl ?? "") + (emailAddress ?? ""));
    }

    /// <summary>
    /// Computes MD5 hash of input string (UTF-8 encoded) and returns lowercase hex.
    /// </summary>
    public static string HashString(string input)
    {
        byte[] inputBytes = Encoding.UTF8.GetBytes(input);
        byte[] hashBytes = MD5.HashData(inputBytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
