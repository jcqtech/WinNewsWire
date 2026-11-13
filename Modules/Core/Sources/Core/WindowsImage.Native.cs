using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace WinNewsWire.Core;

public static partial class WindowsImage
{
    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void CreateStreamOnHGlobal(IntPtr memory, [MarshalAs(UnmanagedType.Bool)] bool deleteOnRelease, out IStream stream);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public uint UnsignedValue;
    }

    [ComImport, Guid("cacaf262-9370-4615-a13b-9f5539da4c0a")]
    private class ImagingFactory { }

    [ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICImagingFactory
    {
        void CreateDecoderFromFilename([MarshalAs(UnmanagedType.LPWStr)] string filename, IntPtr vendor, uint access, uint options, out IWICBitmapDecoder decoder);
        void CreateDecoderFromStream(IStream stream, IntPtr vendor, uint options, out IWICBitmapDecoder decoder);
        void CreateDecoderFromFileHandle(IntPtr file, IntPtr vendor, uint options, out IWICBitmapDecoder decoder);
        void CreateComponentInfo(ref Guid component, out IWICPixelFormatInfo2 info);
        void CreateDecoder(ref Guid format, IntPtr vendor, out IWICBitmapDecoder decoder);
        void CreateEncoder(ref Guid format, IntPtr vendor, out IWICBitmapEncoder encoder);
        void CreatePalette(out IntPtr palette);
        void CreateFormatConverter(out IWICFormatConverter converter);
        void CreateBitmapScaler(out IWICBitmapScaler scaler);
        void CreateBitmapClipper(out IntPtr clipper);
        void CreateBitmapFlipRotator(out IWICBitmapFlipRotator rotator);
    }

    [ComImport, Guid("00000120-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapSource
    {
        void GetSize(out uint width, out uint height);
        void GetPixelFormat(out Guid format);
        void GetResolution(out double dpiX, out double dpiY);
        void CopyPalette(IntPtr palette);
        void CopyPixels(IntPtr rectangle, uint stride, uint bufferSize, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buffer);
    }

    [ComImport, Guid("9edde9e7-8dee-47ea-99df-e6faf2ed44bf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapDecoder
    {
        void QueryCapability(IStream stream, out uint capability);
        void Initialize(IStream stream, uint options);
        void GetContainerFormat(out Guid format);
        void GetDecoderInfo(out IntPtr info);
        void CopyPalette(IntPtr palette);
        void GetMetadataQueryReader(out IWICMetadataQueryReader reader);
        void GetPreview(out IWICBitmapSource preview);
        void GetColorContexts(uint count, IntPtr contexts, out uint actualCount);
        void GetThumbnail(out IWICBitmapSource thumbnail);
        void GetFrameCount(out uint count);
        void GetFrame(uint index, out IWICBitmapFrameDecode frame);
    }

    // COM inheritance requires the base vtable slots to be repeated in each derived interface.
    [ComImport, Guid("3b16811b-6a43-4ec9-a813-3d930c13b940"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapFrameDecode : IWICBitmapSource
    {
        new void GetSize(out uint width, out uint height);
        new void GetPixelFormat(out Guid format);
        new void GetResolution(out double dpiX, out double dpiY);
        new void CopyPalette(IntPtr palette);
        new void CopyPixels(IntPtr rectangle, uint stride, uint bufferSize, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buffer);
        [PreserveSig] int GetMetadataQueryReader(out IWICMetadataQueryReader reader);
        void GetColorContexts(uint count, IntPtr contexts, out uint actualCount);
        void GetThumbnail(out IWICBitmapSource thumbnail);
    }

    [ComImport, Guid("00000301-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICFormatConverter : IWICBitmapSource
    {
        new void GetSize(out uint width, out uint height);
        new void GetPixelFormat(out Guid format);
        new void GetResolution(out double dpiX, out double dpiY);
        new void CopyPalette(IntPtr palette);
        new void CopyPixels(IntPtr rectangle, uint stride, uint bufferSize, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buffer);
        void Initialize(IWICBitmapSource source, ref Guid format, uint dither, IntPtr palette, double alphaThreshold, uint paletteType);
        void CanConvert(ref Guid sourceFormat, ref Guid destinationFormat, [MarshalAs(UnmanagedType.Bool)] out bool canConvert);
    }

    [ComImport, Guid("00000302-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapScaler : IWICBitmapSource
    {
        new void GetSize(out uint width, out uint height);
        new void GetPixelFormat(out Guid format);
        new void GetResolution(out double dpiX, out double dpiY);
        new void CopyPalette(IntPtr palette);
        new void CopyPixels(IntPtr rectangle, uint stride, uint bufferSize, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buffer);
        void Initialize(IWICBitmapSource source, uint width, uint height, uint interpolation);
    }

    [ComImport, Guid("5009834f-2d6a-41ce-9e1b-17c5aff7a782"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapFlipRotator : IWICBitmapSource
    {
        new void GetSize(out uint width, out uint height);
        new void GetPixelFormat(out Guid format);
        new void GetResolution(out double dpiX, out double dpiY);
        new void CopyPalette(IntPtr palette);
        new void CopyPixels(IntPtr rectangle, uint stride, uint bufferSize, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buffer);
        void Initialize(IWICBitmapSource source, uint transform);
    }

    [ComImport, Guid("30989668-e1c9-4597-b395-458eedb808df"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICMetadataQueryReader
    {
        void GetContainerFormat(out Guid format);
        void GetLocation(uint capacity, IntPtr location, out uint actualLength);
        [PreserveSig] int GetMetadataByName([MarshalAs(UnmanagedType.LPWStr)] string name, ref PropVariant value);
        void GetEnumerator(out IntPtr enumerator);
    }

    [ComImport, Guid("00000103-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapEncoder
    {
        void Initialize(IStream stream, uint options);
        void GetContainerFormat(out Guid format);
        void GetEncoderInfo(out IntPtr info);
        void SetColorContexts(uint count, IntPtr contexts);
        void SetPalette(IntPtr palette);
        void SetThumbnail(IWICBitmapSource thumbnail);
        void SetPreview(IWICBitmapSource preview);
        void CreateNewFrame(out IWICBitmapFrameEncode frame, out IPropertyBag2 options);
        void Commit();
        void GetMetadataQueryWriter(out IntPtr writer);
    }

    [ComImport, Guid("00000105-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapFrameEncode
    {
        void Initialize(IPropertyBag2 options);
        void SetSize(uint width, uint height);
        void SetResolution(double dpiX, double dpiY);
        void SetPixelFormat(ref Guid format);
        void SetColorContexts(uint count, IntPtr contexts);
        void SetPalette(IntPtr palette);
        void SetThumbnail(IWICBitmapSource thumbnail);
        void WritePixels(uint lineCount, uint stride, uint bufferSize, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] pixels);
        void WriteSource(IWICBitmapSource source, IntPtr rectangle);
        void Commit();
        void GetMetadataQueryWriter(out IntPtr writer);
    }

    [ComImport, Guid("a9db33a2-af5f-43c7-b679-74f5984b5aa4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICPixelFormatInfo2
    {
        void GetComponentType(out uint type);
        void GetCLSID(out Guid clsid);
        void GetSigningStatus(out uint status);
        void GetAuthor(uint capacity, IntPtr author, out uint actualLength);
        void GetVendorGUID(out Guid vendor);
        void GetVersion(uint capacity, IntPtr version, out uint actualLength);
        void GetSpecVersion(uint capacity, IntPtr version, out uint actualLength);
        void GetFriendlyName(uint capacity, IntPtr name, out uint actualLength);
        void GetFormatGUID(out Guid format);
        void GetColorContext(out IntPtr context);
        void GetBitsPerPixel(out uint bits);
        void GetChannelCount(out uint count);
        void GetChannelMask(uint channel, uint capacity, IntPtr mask, out uint actualLength);
        void SupportsTransparency([MarshalAs(UnmanagedType.Bool)] out bool transparency);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PropertyBagProperty
    {
        public uint Type;
        public ushort VariantType;
        public ushort ClipboardFormat;
        public uint Hint;
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        public Guid ClassId;
    }

    [ComImport, Guid("22f55882-280b-11d0-a8a9-00a0c90c2004"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag2
    {
        void Read(uint count, IntPtr properties, IntPtr errorLog, IntPtr values, IntPtr errors);
        void Write(uint count, [In] ref PropertyBagProperty property, [In, MarshalAs(UnmanagedType.Struct)] ref object value);
    }
}
