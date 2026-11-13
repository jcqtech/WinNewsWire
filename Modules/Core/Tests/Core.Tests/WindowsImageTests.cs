using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text;
using WinNewsWire.Core;
using Xunit;

namespace WinNewsWire.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsImageTests
{
    [Theory]
    [InlineData(1, 48)]
    [InlineData(1.1, 53)]
    [InlineData(1.25, 60)]
    [InlineData(1.333334, 65)]
    [InlineData(1.75, 84)]
    [InlineData(2, 96)]
    [InlineData(3, 144)]
    public void IconBoundIsCeilingOf48TimesTheMaximumScreenScale(double scale, int expected)
        => Assert.Equal(expected, WindowsScreen.MaxIconPixelSize(scale));

    [Fact]
    public void InvalidScaleAndPixelBoundsAreRejected()
    {
        foreach (double scale in new[] { 0, -1, double.NaN, double.PositiveInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => WindowsScreen.MaxIconPixelSize(scale));
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsImage.ScaledImageData([1], 0));
    }

    [Fact]
    public void SingleFrameAtTheExactBoundKeepsItsOriginalBytes()
    {
        var data = Png(96, 31, Red);
        Assert.Same(data, WindowsImage.ScaledImageData(data, 96));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LargeImagesWithOrWithoutAlphaAreResizedToPng(bool alphaChannel)
    {
        var data = Png(2000, 1000, Blue, alphaChannel);
        var scaled = WindowsImage.ScaledImageData(data, 512);
        Assert.Equal((512u, 256u), WindowsImage.LargestFramePixelSize(scaled));
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, scaled[..8]);
        Assert.Equal(ImageLuminanceType.Dark, WindowsImage.ClassifyLuminance(scaled));
    }

    [Fact]
    public void OpaqueSingleFramesAlreadyWithinTheBoundKeepTheirOriginalBytes()
    {
        var data = Png(64, 32, Red, alphaChannel: false);
        Assert.Same(data, WindowsImage.ScaledImageData(data, 64));
    }

    [Fact]
    public void HeaderDimensionsUseTheFirstFrameWithTheLargestDimension()
    {
        var ico = Ico((16, 16, Png(16, 16, Green)), (64, 16, Png(64, 16, Red)), (64, 64, Png(64, 64, Blue)));
        Assert.Equal((64u, 16u), WindowsImage.LargestFramePixelSize(ico));
    }

    [Fact]
    public void HeaderDimensionsDoNotAllocateAFullPixelBuffer()
    {
        var data = Png(4096, 2048, Red);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var size = WindowsImage.LargestFramePixelSize(data);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal((4096u, 2048u), size);
        Assert.InRange(allocated, 0, 1_048_575);
    }

    [Theory]
    [InlineData(192, 96, 96, 48)]
    [InlineData(96, 192, 48, 96)]
    [InlineData(97, 97, 96, 96)]
    [InlineData(1, 200, 1, 96)]
    public void ResamplingUsesRealPixelsAndPreservesAspectRatio(int width, int height, int expectedWidth, int expectedHeight)
    {
        var scaled = WindowsImage.ScaledImageData(Png(width, height, Red), 96);
        var image = ReadPng(scaled);
        Assert.Equal((expectedWidth, expectedHeight), (image.Width, image.Height));
        Assert.All(image.Pixels.Chunk(4), pixel => Assert.Equal(Red, pixel));
    }

    [Fact]
    public void ResamplingRetainsAlphaAndColor()
    {
        byte[] translucentGreen = [0, 255, 0, 128];
        var image = ReadPng(WindowsImage.ScaledImageData(Png(128, 64, translucentGreen), 48));
        Assert.All(image.Pixels.Chunk(4), pixel => Assert.Equal(translucentGreen, pixel));
    }

    [Theory]
    [InlineData(96, 64, 16)]
    [InlineData(32, 32, 8)]
    public void MultiFrameIcoSelectsFirstLargestDimensionRatherThanFirstFrameOrLargestArea(int limit, int width, int height)
    {
        var ico = Ico((16, 16, Png(16, 16, Green)), (64, 16, Png(64, 16, Red)), (64, 64, Png(64, 64, Blue)));
        var image = ReadPng(WindowsImage.ScaledImageData(ico, limit));
        Assert.Equal((width, height), (image.Width, image.Height));
        Assert.All(image.Pixels.Chunk(4), pixel => Assert.Equal(Red, pixel));
    }

    [Fact]
    public void IndexedColorIcoKeepsItsPaletteRatherThanBecomingGrayscale()
    {
        var ico = Ico((16, 16, IndexedIcon(16, 16, 1)), (48, 48, IndexedIcon(48, 48, 0)));
        var image = ReadPng(WindowsImage.ScaledImageData(ico, 96));
        Assert.Equal((48, 48), (image.Width, image.Height));
        Assert.All(image.Pixels.Chunk(4), pixel => Assert.Equal(Red, pixel));
    }

    [Fact]
    public void AnimatedGifIsExtractedToOnePngFrameEvenWithoutResizing()
    {
        var image = ReadPng(WindowsImage.ScaledImageData(AnimatedGif(), 96));
        Assert.Equal((2, 2), (image.Width, image.Height));
        Assert.All(image.Pixels.Chunk(4), pixel => Assert.Equal(Red, pixel));
    }

    [Theory]
    [InlineData(1, "RGBY")]
    [InlineData(2, "GRYB")]
    [InlineData(3, "YBGR")]
    [InlineData(4, "BYRG")]
    [InlineData(5, "RBGY")]
    [InlineData(6, "BRYG")]
    [InlineData(7, "YGBR")]
    [InlineData(8, "GYRB")]
    public void ResizedImagesApplyAllExifOrientationTransforms(int orientation, string expectedCorners)
    {
        var image = ReadPng(WindowsImage.ScaledImageData(Tiff(6, 4, orientation), 3));
        Assert.Equal(orientation < 5 ? (3, 2) : (2, 3), (image.Width, image.Height));
        var corners = new[] { 0, (image.Width - 1) * 4, (image.Height - 1) * image.Width * 4, image.Pixels.Length - 4 };
        for (int index = 0; index < corners.Length; index++)
            Assert.Equal(Color(expectedCorners[index]), image.Pixels.AsSpan(corners[index], 4).ToArray());
    }

    [Fact]
    public void MalformedBodiesAndAValidHeaderWithoutPixelDataAreRejected()
    {
        var png = Png(8, 8, Red);
        foreach (var data in new[] { Array.Empty<byte>(), "not an image"u8.ToArray(), "<html>error</html>"u8.ToArray(), png[..33] })
        {
            Assert.Throws<InvalidDataException>(() => WindowsImage.ScaledImageData(data, 96));
            Assert.Throws<InvalidDataException>(() => WindowsImage.ClassifyLuminance(data));
        }
    }

    [Theory]
    [InlineData(255, 255, ImageLuminanceType.Bright)]
    [InlineData(0, 255, ImageLuminanceType.Dark)]
    [InlineData(128, 255, ImageLuminanceType.Regular)]
    [InlineData(255, 128, ImageLuminanceType.Bright)]
    [InlineData(255, 0, ImageLuminanceType.Regular)]
    public void NativeLuminanceUsesDecodedPremultipliedPixels(byte gray, byte alpha, ImageLuminanceType expected)
    {
        foreach (int size in new[] { 1, 20, 96 })
            Assert.Equal(expected, WindowsImage.ClassifyLuminance(Png(size, size, [gray, gray, gray, alpha])));
    }

    [Fact]
    public async Task IndependentDecodersCanRunOnMultipleBackgroundThreads()
    {
        var data = Png(120, 60, Blue);
        var images = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => WindowsImage.ScaledImageData(data, 48))));
        foreach (var bytes in images)
        {
            var image = ReadPng(bytes);
            Assert.Equal((48, 24), (image.Width, image.Height));
            Assert.All(image.Pixels.Chunk(4), pixel => Assert.Equal(Blue, pixel));
            Assert.Equal(ImageLuminanceType.Dark, WindowsImage.ClassifyLuminance(bytes));
        }
    }

    private static readonly byte[] Red = [255, 0, 0, 255];
    private static readonly byte[] Green = [0, 255, 0, 255];
    private static readonly byte[] Blue = [0, 0, 255, 255];
    private static readonly byte[] Yellow = [255, 255, 0, 255];
    private static byte[] Color(char color) => color switch { 'R' => Red, 'G' => Green, 'B' => Blue, 'Y' => Yellow, _ => throw new ArgumentException(nameof(color)) };

    private static byte[] Png(int width, int height, byte[] color, bool alphaChannel = true)
    {
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = alphaChannel ? (byte)6 : (byte)2;
        WriteChunk(output, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            for (int y = 0; y < height; y++)
            {
                zlib.WriteByte(0);
                for (int x = 0; x < width; x++) zlib.Write(color.AsSpan(0, alphaChannel ? 4 : 3));
            }
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        output.Write(number);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        uint crc = uint.MaxValue;
        foreach (byte value in typeBytes.Concat(data))
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320);
        }
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
        output.Write(number);
    }

    private static (int Width, int Height, byte[] Pixels) ReadPng(byte[] data)
    {
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, data[..8]);
        int width = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16));
        int height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20));
        Assert.Equal(8, data[24]);
        Assert.Equal(6, data[25]);
        using var compressed = new MemoryStream();
        for (int offset = 8; offset < data.Length;)
        {
            int size = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset));
            if (Encoding.ASCII.GetString(data, offset + 4, 4) == "IDAT") compressed.Write(data, offset + 8, size);
            offset += 12 + size;
        }
        compressed.Position = 0;
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        int stride = width * 4;
        var pixels = new byte[stride * height];
        var row = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            int filter = zlib.ReadByte();
            zlib.ReadExactly(row);
            for (int x = 0; x < stride; x++)
            {
                int left = x >= 4 ? pixels[y * stride + x - 4] : 0;
                int up = y > 0 ? pixels[(y - 1) * stride + x] : 0;
                int upperLeft = y > 0 && x >= 4 ? pixels[(y - 1) * stride + x - 4] : 0;
                int predictor = filter switch
                {
                    0 => 0, 1 => left, 2 => up, 3 => (left + up) / 2, 4 => Paeth(left, up, upperLeft),
                    _ => throw new InvalidDataException("Unexpected PNG filter.")
                };
                pixels[y * stride + x] = unchecked((byte)(row[x] + predictor));
            }
        }
        return (width, height, pixels);
    }

    private static int Paeth(int left, int up, int upperLeft)
    {
        int prediction = left + up - upperLeft;
        int a = Math.Abs(prediction - left), b = Math.Abs(prediction - up), c = Math.Abs(prediction - upperLeft);
        return a <= b && a <= c ? left : b <= c ? up : upperLeft;
    }

    private static byte[] Ico(params (int Width, int Height, byte[] Data)[] frames)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)frames.Length);
        int offset = 6 + 16 * frames.Length;
        foreach (var frame in frames)
        {
            writer.Write((byte)frame.Width);
            writer.Write((byte)frame.Height);
            bool indexed = frame.Data[0] != 137;
            writer.Write((byte)(indexed ? 2 : 0));
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)(indexed ? 8 : 32));
            writer.Write(frame.Data.Length);
            writer.Write(offset);
            offset += frame.Data.Length;
        }
        foreach (var frame in frames) writer.Write(frame.Data);
        return output.ToArray();
    }

    private static byte[] IndexedIcon(int width, int height, byte paletteIndex)
    {
        int stride = (width + 3) / 4 * 4;
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write(40);
        writer.Write(width);
        writer.Write(height * 2);
        writer.Write((ushort)1);
        writer.Write((ushort)8);
        writer.Write(0);
        writer.Write(stride * height);
        writer.Write(0); writer.Write(0);
        writer.Write(2); writer.Write(0);
        writer.Write(new byte[] { 0, 0, 255, 0, 255, 0, 0, 0 });
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++) writer.Write(paletteIndex);
            writer.Write(new byte[stride - width]);
        }
        writer.Write(new byte[(width + 31) / 32 * 4 * height]);
        return output.ToArray();
    }

    private static byte[] AnimatedGif()
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write("GIF89a"u8);
        writer.Write((ushort)2); writer.Write((ushort)2);
        writer.Write(new byte[] { 0x80, 0, 0, 255, 0, 0, 0, 0, 255 });
        for (int frame = 0; frame < 2; frame++)
        {
            writer.Write(new byte[] { 0x21, 0xf9, 4, 0, 10, 0, 0, 0, 0x2c });
            writer.Write(0);
            writer.Write((ushort)2); writer.Write((ushort)2);
            writer.Write(new byte[] { 0, 2 });
            var codes = new[] { 4, frame, 4, frame, 4, frame, 4, frame, 5 };
            uint packed = 0;
            for (int index = 0; index < codes.Length; index++) packed |= (uint)codes[index] << (index * 3);
            writer.Write((byte)4);
            writer.Write(packed);
            writer.Write((byte)0);
        }
        writer.Write((byte)0x3b);
        return output.ToArray();
    }

    private static byte[] Tiff(int width, int height, int orientation)
    {
        const int entries = 11;
        const int bitsOffset = 8 + 2 + entries * 12 + 4;
        const int pixelsOffset = bitsOffset + 6;
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write((ushort)0x4949);
        writer.Write((ushort)42);
        writer.Write(8);
        writer.Write((ushort)entries);
        void Entry(ushort tag, ushort type, int count, int value)
        {
            writer.Write(tag); writer.Write(type); writer.Write(count); writer.Write(value);
        }
        Entry(256, 4, 1, width);
        Entry(257, 4, 1, height);
        Entry(258, 3, 3, bitsOffset);
        Entry(259, 3, 1, 1);
        Entry(262, 3, 1, 2);
        Entry(273, 4, 1, pixelsOffset);
        Entry(274, 3, 1, orientation);
        Entry(277, 3, 1, 3);
        Entry(278, 4, 1, height);
        Entry(279, 4, 1, width * height * 3);
        Entry(284, 3, 1, 1);
        writer.Write(0);
        writer.Write((ushort)8); writer.Write((ushort)8); writer.Write((ushort)8);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                writer.Write((y < height / 2 ? x < width / 2 ? Red : Green : x < width / 2 ? Blue : Yellow), 0, 3);
        return output.ToArray();
    }
}
