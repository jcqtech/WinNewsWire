using Xunit;

namespace WinNewsWire.Core.Tests;

/// <summary>RSCore/Tests/RSCoreTests/HMACTests.swift.</summary>
public class HMACTests
{
    [Fact]
    public void PinnedVectorJefe()
        => Assert.Equal("548329b620689206afea507f732f80f41a4c1a75", "what do you want for nothing?".HmacUsingSha1("Jefe"));

    [Fact]
    public void EmptyKeyEmptyMessage()
        => Assert.Equal("fbdb1d1b18aa6c08324b7d64b71fb76370690e1d", "".HmacUsingSha1(""));

    [Fact]
    public void NonAsciiEmojiMessage()
        => Assert.Equal("442de3a50b8dc6ba238e6049059c4d069d0f2e38", "\U0001f642".HmacUsingSha1("k"));

    [Fact]
    public void OutputFormat()
        => Assert.Matches("^[0-9a-f]{40}$", "arbitrary input".HmacUsingSha1("arbitrary key"));

    [Fact]
    public void DifferentKeysDiffer()
        => Assert.NotEqual("The quick brown fox".HmacUsingSha1("key-one"), "The quick brown fox".HmacUsingSha1("key-two"));

    [Fact]
    public void Deterministic()
        => Assert.Equal("https://example.com/article/42".HmacUsingSha1("client-secret"),
            "https://example.com/article/42".HmacUsingSha1("client-secret"));
}
