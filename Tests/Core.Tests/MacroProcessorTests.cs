using System.Collections.Generic;
using WinNewsWire.Core;
using Xunit;

namespace WinNewsWire.Core.Tests;

/// <summary>
/// Port of NetNewsWire RSCore's <c>MacroProcessorTests.swift</c>. Exercises
/// the template substitution helper used by the article renderer.
/// </summary>
public class MacroProcessorTests
{
    private static readonly IReadOnlyDictionary<string, string> Subs =
        new Dictionary<string, string> { ["one"] = "1", ["two"] = "2" };

    [Theory]
    [InlineData("foo [[one]] bar [[two]] baz", "foo 1 bar 2 baz")]
    [InlineData("[[one]] foo [[two]] bar",     "1 foo 2 bar")]
    [InlineData("foo [[one]] bar [[two]]",     "foo 1 bar 2")]
    public void MacroProcessorReplacesKnownKeys(string template, string expected)
    {
        Assert.Equal(expected, MacroProcessor.RenderedTextWith(template, Subs));
    }

    [Fact]
    public void NonexistentKeyIsLeftIntact()
    {
        var template = "foo [[nonexistent]] bar";
        Assert.Equal(template, MacroProcessor.RenderedTextWith(template, Subs));
    }

    [Fact]
    public void EqualDelimitersAreAllowed()
    {
        var template = "foo |one| bar |two| baz";
        var result = MacroProcessor.RenderedTextWith(template, Subs,
            macroStart: "|", macroEnd: "|");
        Assert.Equal("foo 1 bar 2 baz", result);
    }

    [Fact]
    public void EmptyStartDelimiterThrows()
    {
        Assert.Throws<MacroProcessorException>(() =>
            MacroProcessor.RenderedTextWith("foo bar", Subs, macroStart: string.Empty));
    }

    [Fact]
    public void EmptyEndDelimiterThrows()
    {
        Assert.Throws<MacroProcessorException>(() =>
            MacroProcessor.RenderedTextWith("foo bar", Subs, macroEnd: string.Empty));
    }

    [Fact]
    public void MacroInSubstitutionIsNotRecursive()
    {
        var subs = new Dictionary<string, string> { ["one"] = "[[two]]", ["two"] = "2" };
        // Swift result: "foo [[two]] bar" — the [[two]] inside the substitution
        // should NOT be expanded to "2".
        Assert.Equal("foo [[two]] bar",
            MacroProcessor.RenderedTextWith("foo [[one]] bar", subs));
    }

    [Fact]
    public void UnterminatedMacroIsLeftIntact()
    {
        // Unterminated macro should leave the original token in place rather
        // than swallow the rest of the template.
        Assert.Equal("foo [[one bar",
            MacroProcessor.RenderedTextWith("foo [[one bar", Subs));
    }
}
