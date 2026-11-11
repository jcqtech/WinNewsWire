using Xunit;

namespace WinNewsWire.Core.Tests;

/// <summary>RSCore/Tests/RSCoreTests/MD5Tests.swift.</summary>
public class MD5Tests
{
    [Fact]
    public void EmptyString() => Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", "".Md5Hex());

    [Theory]
    [InlineData("abc", "900150983cd24fb0d6963f7d28e17f72")]
    [InlineData("The quick brown fox jumps over the lazy dog", "9e107d9d372bb6826bd81d3542a419d6")]
    public void KnownVector(string input, string expected) => Assert.Equal(expected, input.Md5Hex());

    [Fact]
    public void FormatIs32LowercaseHex() => Assert.Matches("^[0-9a-f]{32}$", "arbitrary input".Md5Hex());
}
