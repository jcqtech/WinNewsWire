using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;

namespace WinNewsWire.Core;

/// <summary>Windows Imaging Component adapter for RSImage.scaledImageData.</summary>
[SupportedOSPlatform("windows")]
public static partial class WindowsImage
{
    private static readonly Guid PngFormat = new("1b7cfaf4-713f-473c-bbcd-6137425faeaf");
    private static readonly Guid Bgra32 = new("6fddc324-4e03-4bfe-b185-3d77768dc90f");
    private static readonly Guid PremultipliedBgra32 = new("6fddc324-4e03-4bfe-b185-3d77768dc910");

    public static byte[] ScaledImageData(byte[] data, int maxPixelSize)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPixelSize);
        if (data.Length == 0) throw new InvalidDataException("The image is empty.");
        try
        {
            using var resources = new ComResources();
            var factory = resources.Add((IWICImagingFactory)new ImagingFactory());
            CreateStreamOnHGlobal(IntPtr.Zero, true, out var input);
            resources.Add(input);
            input.Write(data, data.Length, IntPtr.Zero);
            input.Seek(0, 0, IntPtr.Zero);
            factory.CreateDecoderFromStream(input, IntPtr.Zero, 0, out var decoder);
            resources.Add(decoder);
            var largest = LargestFrame(decoder, out uint count);
            uint largestDimension = Math.Max(largest.Width, largest.Height);
            decoder.GetFrame(largest.Index, out var frame);
            resources.Add(frame);
            var source = ConvertPixels(factory, frame, PremultipliedBgra32, resources);
            bool needsResize = largestDimension > maxPixelSize;
            if (!needsResize && count == 1)
            {
                ReadPixels(source, out _, out _);
                return data;
            }

            if (needsResize)
            {
                double ratio = (double)maxPixelSize / largestDimension;
                uint width = Math.Max(1, (uint)Math.Round(largest.Width * ratio, MidpointRounding.AwayFromZero));
                uint height = Math.Max(1, (uint)Math.Round(largest.Height * ratio, MidpointRounding.AwayFromZero));
                factory.CreateBitmapScaler(out var scaler);
                resources.Add(scaler);
                scaler.Initialize(source, width, height, 3);
                source = scaler;
            }
            uint transform = OrientationTransform(frame, resources);
            if (transform != 0)
            {
                factory.CreateBitmapFlipRotator(out var rotator);
                resources.Add(rotator);
                rotator.Initialize(source, transform);
                source = rotator;
            }
            return EncodeImage(factory, source, resources);
        }
        catch (COMException error) when (IsImageError(error.HResult))
        {
            throw new InvalidDataException("Windows could not decode or resize the image.", error);
        }
        catch (ArgumentException error)
        {
            throw new InvalidDataException("Windows rejected the image data.", error);
        }
    }

    public static (uint Width, uint Height) LargestFramePixelSize(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0) throw new InvalidDataException("The image is empty.");
        try
        {
            using var resources = new ComResources();
            var factory = resources.Add((IWICImagingFactory)new ImagingFactory());
            CreateStreamOnHGlobal(IntPtr.Zero, true, out var input);
            resources.Add(input);
            input.Write(data, data.Length, IntPtr.Zero);
            input.Seek(0, 0, IntPtr.Zero);
            factory.CreateDecoderFromStream(input, IntPtr.Zero, 0, out var decoder);
            resources.Add(decoder);
            var largest = LargestFrame(decoder, out _);
            return (largest.Width, largest.Height);
        }
        catch (COMException error) when (IsImageError(error.HResult))
        {
            throw new InvalidDataException("Windows could not read the image dimensions.", error);
        }
    }

    public static byte[] GrayscaleImageData(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0) throw new InvalidDataException("The image is empty.");
        try
        {
            using var resources = new ComResources();
            var factory = resources.Add((IWICImagingFactory)new ImagingFactory());
            CreateStreamOnHGlobal(IntPtr.Zero, true, out var input);
            resources.Add(input);
            input.Write(data, data.Length, IntPtr.Zero);
            input.Seek(0, 0, IntPtr.Zero);
            factory.CreateDecoderFromStream(input, IntPtr.Zero, 0, out var decoder);
            resources.Add(decoder);
            var largest = LargestFrame(decoder, out _);
            decoder.GetFrame(largest.Index, out var frame);
            resources.Add(frame);
            var source = ConvertPixels(factory, frame, Bgra32, resources);
            uint transform = OrientationTransform(frame, resources);
            if (transform != 0)
            {
                factory.CreateBitmapFlipRotator(out var rotator);
                resources.Add(rotator);
                rotator.Initialize(source, transform);
                source = rotator;
            }
            var pixels = ReadPixels(source, out _, out _);
            for (int offset = 0; offset < pixels.Length; offset += 4)
            {
                byte gray = pixels[offset + 3] == 0 ? (byte)0 : (byte)Math.Round(
                    pixels[offset + 2] * 0.2126 + pixels[offset + 1] * 0.7152 + pixels[offset] * 0.0722,
                    MidpointRounding.AwayFromZero);
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = gray;
            }
            return EncodeImage(factory, source, resources, pixels);
        }
        catch (COMException error) when (IsImageError(error.HResult))
        {
            throw new InvalidDataException("Windows could not convert the icon to grayscale.", error);
        }
    }

    private static (uint Index, uint Width, uint Height) LargestFrame(IWICBitmapDecoder decoder, out uint count)
    {
        decoder.GetFrameCount(out count);
        uint largestIndex = 0, largestWidth = 0, largestHeight = 0, largestDimension = 0;
        for (uint index = 0; index < count; index++)
        {
            IWICBitmapFrameDecode? candidate = null;
            try
            {
                decoder.GetFrame(index, out candidate);
                candidate.GetSize(out uint width, out uint height);
                uint dimension = Math.Max(width, height);
                if (width > 0 && height > 0 && dimension > largestDimension)
                    (largestIndex, largestWidth, largestHeight, largestDimension) = (index, width, height, dimension);
            }
            catch (COMException error) when (IsImageError(error.HResult))
            {
                Trace.TraceWarning($"Could not read image frame {index}: {error.Message}");
            }
            finally { if (candidate is not null) Marshal.ReleaseComObject(candidate); }
        }
        if (largestDimension == 0) throw new InvalidDataException("The image has no decodable frames.");
        return (largestIndex, largestWidth, largestHeight);
    }

    private static bool IsImageError(int error)
        => (unchecked((uint)error) & 0xffff0000) == 0x88980000 || error == unchecked((int)0x80004005);

    private static IWICBitmapSource ConvertPixels(IWICImagingFactory factory, IWICBitmapSource source, Guid format, ComResources resources)
    {
        factory.CreateFormatConverter(out var converter);
        resources.Add(converter);
        converter.Initialize(source, ref format, 0, IntPtr.Zero, 0, 0);
        return converter;
    }

    public static ImageLuminanceType ClassifyLuminance(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0) throw new InvalidDataException("The image is empty.");
        try
        {
            using var resources = new ComResources();
            var factory = resources.Add((IWICImagingFactory)new ImagingFactory());
            CreateStreamOnHGlobal(IntPtr.Zero, true, out var input);
            resources.Add(input);
            input.Write(data, data.Length, IntPtr.Zero);
            input.Seek(0, 0, IntPtr.Zero);
            factory.CreateDecoderFromStream(input, IntPtr.Zero, 0, out var decoder);
            resources.Add(decoder);
            decoder.GetFrame(0, out var frame);
            resources.Add(frame);
            var source = ConvertPixels(factory, frame, PremultipliedBgra32, resources);
            uint transform = OrientationTransform(frame, resources);
            if (transform != 0)
            {
                factory.CreateBitmapFlipRotator(out var rotator);
                resources.Add(rotator);
                rotator.Initialize(source, transform);
                source = rotator;
            }
            var pixels = ReadPixels(source, out uint width, out uint height);
            return ImageLuminance.CalculateBgraPremultiplied(pixels, checked((int)width), checked((int)height), checked((int)width * 4));
        }
        catch (COMException error) when (IsImageError(error.HResult))
        {
            throw new InvalidDataException("Windows could not decode the icon pixels.", error);
        }
    }

    private static byte[] ReadPixels(IWICBitmapSource source, out uint width, out uint height)
    {
        source.GetSize(out width, out height);
        int stride = checked((int)width * 4);
        var pixels = new byte[checked(stride * (int)height)];
        source.CopyPixels(IntPtr.Zero, (uint)stride, (uint)pixels.Length, pixels);
        return pixels;
    }

    private static uint OrientationTransform(IWICBitmapFrameDecode frame, ComResources resources)
    {
        int result = frame.GetMetadataQueryReader(out var reader);
        if (result == unchecked((int)0x88982f81)) return 0;
        Marshal.ThrowExceptionForHR(result);
        resources.Add(reader);
        foreach (string path in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
        {
            var value = new PropVariant();
            try
            {
                result = reader.GetMetadataByName(path, ref value);
                if (result is unchecked((int)0x88982f40) or unchecked((int)0x88982f41)) continue;
                Marshal.ThrowExceptionForHR(result);
                if (value.Type is not (18 or 19)) continue;
                return value.UnsignedValue switch
                {
                    2 => 8, 3 => 2, 4 => 16, 5 => 3 | 8, 6 => 1, 7 => 1 | 8, 8 => 3, _ => 0
                };
            }
            finally { Marshal.ThrowExceptionForHR(PropVariantClear(ref value)); }
        }
        return 0;
    }

    private static byte[] EncodeImage(IWICImagingFactory factory, IWICBitmapSource source, ComResources resources, byte[]? pixels = null)
    {
        var pixelFormat = Bgra32;
        source = ConvertPixels(factory, source, pixelFormat, resources);
        source.GetSize(out uint width, out uint height);
        CreateStreamOnHGlobal(IntPtr.Zero, true, out var output);
        resources.Add(output);
        var format = PngFormat;
        factory.CreateEncoder(ref format, IntPtr.Zero, out var encoder);
        resources.Add(encoder);
        encoder.Initialize(output, 2);
        encoder.CreateNewFrame(out var frame, out var options);
        resources.Add(frame);
        if (options is null) throw new InvalidDataException("The image encoder did not provide its options.");
        resources.Add(options);
        frame.Initialize(options);
        frame.SetSize(width, height);
        frame.SetPixelFormat(ref pixelFormat);
        if (pixels is null) frame.WriteSource(source, IntPtr.Zero);
        else
        {
            if (pixelFormat != Bgra32) throw new InvalidDataException("The PNG encoder rejected 32-bit BGRA pixels.");
            frame.WritePixels(height, checked(width * 4), checked((uint)pixels.Length), pixels);
        }
        frame.Commit();
        encoder.Commit();
        output.Stat(out var stats, 1);
        var bytes = new byte[checked((int)stats.cbSize)];
        output.Seek(0, 0, IntPtr.Zero);
        output.Read(bytes, bytes.Length, IntPtr.Zero);
        return bytes;
    }

    private sealed class ComResources : IDisposable
    {
        private readonly List<object> _objects = [];
        public T Add<T>(T value) where T : class { _objects.Add(value); return value; }
        public void Dispose()
        {
            for (int index = _objects.Count - 1; index >= 0; index--) Marshal.ReleaseComObject(_objects[index]);
        }
    }
}
