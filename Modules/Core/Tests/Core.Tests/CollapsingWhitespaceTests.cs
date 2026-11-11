using Xunit;

namespace WinNewsWire.Core.Tests;

/// <summary>RSCore/Tests/RSCoreTests/CollapsingWhitespaceTests.swift.</summary>
public class CollapsingWhitespaceTests
{
    [Fact]
    public void EmptyString() => Assert.Equal("", "".CollapsingWhitespace());

    [Theory]
    [InlineData("   ")]
    [InlineData("\t\t\t")]
    [InlineData("\n\n")]
    [InlineData("\r\n")]
    [InlineData(" \t\n\r")]
    public void OnlyWhitespaceCollapsesToEmpty(string input)
        => Assert.Equal("", input.CollapsingWhitespace());

    [Fact]
    public void SingleCharacter() => Assert.Equal("x", "x".CollapsingWhitespace());

    [Fact]
    public void LeadingWhitespaceIsStripped() => Assert.Equal("hello", "   hello".CollapsingWhitespace());

    [Fact]
    public void TrailingWhitespaceIsStripped() => Assert.Equal("hello", "hello   ".CollapsingWhitespace());

    [Fact]
    public void LeadingAndTrailingWhitespaceStripped() => Assert.Equal("hello", "   hello   ".CollapsingWhitespace());

    [Fact]
    public void AlreadyCleanStringPassesThrough() => Assert.Equal("one two three", "one two three".CollapsingWhitespace());

    [Fact]
    public void MultiSpaceRunsCollapseToSingleSpace() => Assert.Equal("one two", "one     two".CollapsingWhitespace());

    [Theory]
    [InlineData("a b", "a b")]
    [InlineData("a\tb", "a b")]
    [InlineData("a\nb", "a b")]
    [InlineData("a\vb", "a b")]
    [InlineData("a\fb", "a b")]
    [InlineData("a\rb", "a b")]
    [InlineData("a \t\n\v\f\r b", "a b")]
    public void AsciiWhitespaceCharacters(string input, string expected)
        => Assert.Equal(expected, input.CollapsingWhitespace());

    [Fact]
    public void NonBreakingSpaceIsPreserved() => Assert.Equal("a\u00a0b", "a\u00a0b".CollapsingWhitespace());

    [Fact]
    public void UnicodeContentPassesThrough()
        => Assert.Equal("Caf\u00e9 r\u00e9sum\u00e9 \U0001f389", "Caf\u00e9 r\u00e9sum\u00e9 \U0001f389".CollapsingWhitespace());

    [Fact]
    public void UnicodeContentWithInternalWhitespaceIsCollapsed()
        => Assert.Equal("Caf\u00e9 r\u00e9sum\u00e9", "Caf\u00e9   r\u00e9sum\u00e9".CollapsingWhitespace());

    [Fact]
    public void MixedWhitespaceAndText()
        => Assert.Equal("lots of random whitespace", "   lots\t\tof   random\n\nwhitespace\r\n".CollapsingWhitespace());
}
