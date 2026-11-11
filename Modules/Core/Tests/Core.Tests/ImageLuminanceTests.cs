using WinNewsWire.Core;
using Xunit;

namespace Core.Tests;

public sealed class ImageLuminanceTests
{
    [Fact] public void WhiteImageWithTransparencyIsBright()
        => Assert.Equal(ImageLuminanceType.Bright, Calculate(MakeImage(100, 0.5, 255), 100));
    [Fact] public void FullyWhiteOpaqueIsBright()
        => Assert.Equal(ImageLuminanceType.Bright, Calculate(MakeImage(100, 1, 255), 100));
    [Fact] public void FullyBlackOpaqueIsDark()
        => Assert.Equal(ImageLuminanceType.Dark, Calculate(MakeImage(100, 1, 0), 100));
    [Fact] public void MidGrayOpaqueIsRegular()
        => Assert.Equal(ImageLuminanceType.Regular, Calculate(MakeImage(100, 1, 128), 100));
    [Fact] public void FullyTransparentIsRegular()
        => Assert.Equal(ImageLuminanceType.Regular, Calculate(MakeImage(100, 0, 255), 100));

    [Fact]
    public void SemitransparentPixelsAreUnpremultiplied()
    {
        var pixels = MakeImage(20, 1, 128);
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 128;
        Assert.Equal(ImageLuminanceType.Bright, Calculate(pixels, 20));
    }

    [Theory]
    [InlineData(39, ImageLuminanceType.Dark)]
    [InlineData(40, ImageLuminanceType.Regular)]
    [InlineData(180, ImageLuminanceType.Regular)]
    [InlineData(181, ImageLuminanceType.Bright)]
    public void FinalClassificationUsesTheNativeThresholds(byte gray, ImageLuminanceType expected)
        => Assert.Equal(expected, Calculate(MakeImage(20, 0.1, gray), 20));

    [Fact]
    public void PixelChannelOrderMatchesBgra()
        => Assert.Equal(ImageLuminanceType.Dark, ImageLuminance.CalculateBgraPremultiplied([255, 0, 0, 255], 1, 1, 4));

    [Fact]
    public void IncompleteBuffersAreRejectedExplicitly()
        => Assert.Throws<ArgumentException>(() => ImageLuminance.CalculateBgraPremultiplied([0, 0, 0], 1, 1, 4));

    private static ImageLuminanceType Calculate(byte[] pixels, int size)
        => ImageLuminance.CalculateBgraPremultiplied(pixels, size, size, size * 4);
    private static byte[] MakeImage(int size, double opaqueFraction, byte gray)
    {
        var pixels = new byte[size * size * 4];
        var height = (int)(size * opaqueFraction);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < size; x++)
            {
                var offset = (y * size + x) * 4;
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = gray;
                pixels[offset + 3] = 255;
            }
        return pixels;
    }
}
