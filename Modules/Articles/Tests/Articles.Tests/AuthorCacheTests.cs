using Xunit;

namespace WinNewsWire.Articles.Tests;

/// <summary>Articles/Tests/ArticlesTests/AuthorCacheTests.swift.</summary>
public class AuthorCacheTests
{
    [Fact]
    public void AddReturnsExistingValueOnSecondCall()
    {
        var cache = new AuthorCache();
        var original = MakeAuthor("Jane", "https://example.com/jane");
        var copy = MakeAuthor("Jane", "https://example.com/jane");
        Assert.Equal(original.AuthorID, copy.AuthorID);
        cache.Add([original]);
        cache.Add([copy]);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void AddSetReturnsCanonicalSet()
    {
        var cache = new AuthorCache();
        var result = cache.Add([MakeAuthor("Jane"), MakeAuthor("John")]);
        Assert.Equal(2, result.Count);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void ClearEmptiesCache()
    {
        var cache = new AuthorCache();
        cache.Add([MakeAuthor("Jane")]);
        Assert.Equal(1, cache.Count);
        cache.Clear();
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void DistinctAuthorsCoexist()
    {
        var cache = new AuthorCache();
        var jane = MakeAuthor("Jane");
        var john = MakeAuthor("John");
        Assert.NotEqual(jane.AuthorID, john.AuthorID);
        cache.Add([jane, john]);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void RoundTripThroughAuthorsWithJson()
    {
        AuthorCache.Shared.Clear();
        var jane = MakeAuthor("Jane", "https://example.com/jane");
        var json = Author.ToJson([jane]);
        Assert.NotNull(json);
        Author.FromJson(json);
        Author.FromJson(json);
        Assert.Equal(1, AuthorCache.Shared.Count);
        AuthorCache.Shared.Clear();
    }

    private static Author MakeAuthor(string? name = null, string? url = null, string? avatarUrl = null, string? emailAddress = null)
        => Author.Create(null, name, url, avatarUrl, emailAddress)!;
}
