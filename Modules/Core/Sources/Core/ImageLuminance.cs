namespace WinNewsWire.Core;

public enum ImageLuminanceType { Regular, Bright, Dark }

public static class ImageLuminance
{
    public static ImageLuminanceType CalculateBgraPremultiplied(ReadOnlySpan<byte> pixels, int width, int height, int stride)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (stride < (long)width * 4 || (height - 1L) * stride + width * 4L > pixels.Length)
            throw new ArgumentException("The pixel buffer does not contain the requested BGRA image.", nameof(pixels));

        const int size = 20;
        const int totalPixels = size * size;
        double totalLuminance = 0;
        var processed = 0;
        for (var y = 0; y < size; y++)
        {
            var sourceY = Math.Min(height - 1, (int)((2L * y + 1) * height / (2 * size)));
            for (var x = 0; x < size; x++)
            {
                var sourceX = Math.Min(width - 1, (int)((2L * x + 1) * width / (2 * size)));
                var offset = sourceY * stride + sourceX * 4;
                var alpha = pixels[offset + 3];
                if (alpha == 0) continue;
                var blue = pixels[offset] * 255.0 / alpha;
                var green = pixels[offset + 1] * 255.0 / alpha;
                var red = pixels[offset + 2] * 255.0 / alpha;
                totalLuminance += 0.299 * red + 0.587 * green + 0.114 * blue;
                processed++;
                if (processed == totalPixels / 4)
                {
                    var average = totalLuminance / processed;
                    if (average < 30) return ImageLuminanceType.Dark;
                    if (average > 190) return ImageLuminanceType.Bright;
                }
            }
        }
        if (processed == 0) return ImageLuminanceType.Regular;
        var luminance = totalLuminance / processed;
        return luminance < 40 ? ImageLuminanceType.Dark : luminance > 180 ? ImageLuminanceType.Bright : ImageLuminanceType.Regular;
    }
}
