using WinNewsWire.AppShared.Timeline;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

public sealed class ArticleArrayTests
{
    [Theory]
    [InlineData(0, 0, false, new int[0])]
    [InlineData(0, 0, true, new int[0])]
    [InlineData(3, 0, false, new[] { 0, 1, 2 })]
    [InlineData(3, 1, false, new[] { 1, 2 })]
    [InlineData(3, 1, true, new[] { 1, 2, 0 })]
    [InlineData(3, 3, false, new int[0])]
    [InlineData(3, 3, true, new[] { 0, 1, 2 })]
    [InlineData(3, int.MaxValue, true, new[] { 0, 1, 2 })]
    public void OrderedRowsMatchTheNativeTraversal(int count, int start, bool wrap, int[] expected)
        => Assert.Equal(expected, ArticleArray.OrderedRowIndexes(count, start, wrap));

    [Theory]
    [InlineData(new bool[0], -1, false, null)]
    [InlineData(new[] { true, false, false }, -1, false, 1)]
    [InlineData(new[] { true, false, false }, 1, false, 2)]
    [InlineData(new[] { false, true, false }, 2, false, null)]
    [InlineData(new[] { false, true, false }, 2, true, 0)]
    [InlineData(new[] { true, false, true }, 1, true, 1)]
    [InlineData(new[] { true, true, true }, 1, true, null)]
    [InlineData(new[] { false, true }, int.MaxValue, true, 0)]
    public void NextUnreadSkipsReadRowsAndWrapsOnlyWhenRequested(bool[] read, int selected, bool wrap, int? expected)
        => Assert.Equal(expected, ArticleArray.RowOfNextUnreadArticle(read, selected, wrap, value => value));

    [Fact]
    public void InvalidNegativeIndexesAreExplicit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ArticleArray.OrderedRowIndexes(-1, 0, false).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => ArticleArray.OrderedRowIndexes(1, -1, false).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => ArticleArray.RowOfNextUnreadArticle(new[] { false }, -2, false, value => value));
    }
}
