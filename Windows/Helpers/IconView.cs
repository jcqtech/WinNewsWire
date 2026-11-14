using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinNewsWire.Core;

namespace WinNewsWire.Helpers;

public sealed class IconView : UserControl
{
#if PARITY_PROBE
    internal static int ProbeRenderReadbacks;
    internal static int ProbePixelDecodes;
    internal static int ProbeLuminanceCacheCount { get { lock (LuminanceGate) return LuminanceCache.Count; } }
    internal bool ProbeLuminanceReady => _measuredGeneration == _generation;
    internal ImageLuminanceType ProbeLuminance => _luminance;
    internal Brush? ProbeBackground => _border.Background;
#endif
    private sealed record LuminanceRequest(DateTime Modified, long Size, Lazy<ImageLuminanceType> Result);
    private static readonly object LuminanceGate = new();
    private static readonly Dictionary<string, LuminanceRequest> LuminanceCache = new(StringComparer.OrdinalIgnoreCase);
    public static readonly DependencyProperty ImageSourceProperty =
        DependencyProperty.Register(nameof(ImageSource), typeof(ImageSource), typeof(IconView),
            new PropertyMetadata(null, SourceChanged));
    public ImageSource? ImageSource
    {
        get => (ImageSource?)GetValue(ImageSourceProperty);
        set => SetValue(ImageSourceProperty, value);
    }

    private readonly Image _image = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _border = new() { CornerRadius = new CornerRadius(4) };
    private readonly Style _backgroundStyle;
    private ImageLuminanceType _luminance;
    private int _generation;
    private int _measuringGeneration = -1;
    private int _measuredGeneration = -1;

    public IconView()
    {
        _backgroundStyle = (Style)Application.Current.Resources["FeedIconBackgroundStyle"];
        _border.Child = _image;
        Content = _border;
        _image.ImageOpened += async (_, _) => await UpdateImageAsync();
        _image.SizeChanged += (_, _) => UpdateBackground();
        Loaded += async (_, _) => await UpdateImageAsync();
        ActualThemeChanged += (_, _) => UpdateBackground();
        SizeChanged += (_, _) => UpdateBackground();
    }

    private static void SourceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var view = (IconView)sender;
        view._generation++;
        view._luminance = ImageLuminanceType.Regular;
        view._image.MaxWidth = view._image.MaxHeight = double.PositiveInfinity;
        view._image.Source = (ImageSource?)args.NewValue;
        view.UpdateBackground();
    }

    internal static void EmptyCache()
    {
        SingleLineTextFieldSizer.VerifyAccess();
        lock (LuminanceGate) LuminanceCache.Clear();
    }

    private static Task<ImageLuminanceType> FileLuminanceAsync(Uri uri) => Task.Run(() =>
    {
        var file = new FileInfo(uri.LocalPath);
        var modified = file.LastWriteTimeUtc;
        long size = file.Length;
        LuminanceRequest request;
        lock (LuminanceGate)
        {
            if (!LuminanceCache.TryGetValue(file.FullName, out var existing)
                || existing.Modified != modified || existing.Size != size)
            {
                request = new(modified, size, new(() => DecodeLuminance(file.FullName)));
                LuminanceCache[file.FullName] = request;
            }
            else request = existing;
        }
        try { return request.Result.Value; }
        catch
        {
            lock (LuminanceGate)
                if (LuminanceCache.TryGetValue(file.FullName, out var current) && ReferenceEquals(current, request))
                    LuminanceCache.Remove(file.FullName);
            throw;
        }
    });

    private static ImageLuminanceType DecodeLuminance(string path)
    {
#if PARITY_PROBE
        Interlocked.Increment(ref ProbePixelDecodes);
#endif
        return WindowsImage.ClassifyLuminance(File.ReadAllBytes(path));
    }

    private async Task UpdateImageAsync()
    {
        if (!IsLoaded || ImageSource is null) return;
        var generation = _generation;
        if (_measuringGeneration == generation || _measuredGeneration == generation) return;
        if (ImageSource is BitmapImage bitmap)
        {
            if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0) return;
            _image.MaxWidth = bitmap.PixelWidth;
            _image.MaxHeight = bitmap.PixelHeight;
        }
        var fileUri = (ImageSource as BitmapImage)?.UriSource is { IsFile: true } uri ? uri : null;
        bool hasGrayscaleLuminance = GrayscaleIconCache.TryGetLuminance(ImageSource, out var grayscaleLuminance);
        if (fileUri is null && !hasGrayscaleLuminance && (_image.ActualWidth <= 0 || _image.ActualHeight <= 0)) return;
        _measuringGeneration = generation;
        try
        {
            ImageLuminanceType luminance;
            if (hasGrayscaleLuminance) luminance = grayscaleLuminance;
            else if (fileUri is not null) luminance = await FileLuminanceAsync(fileUri);
            else
            {
#if PARITY_PROBE
                ProbeRenderReadbacks++;
#endif
                var raster = new RenderTargetBitmap();
                await raster.RenderAsync(_image, 20, 20);
                var pixels = await raster.GetPixelsAsync();
                if (raster.PixelWidth <= 0 || raster.PixelHeight <= 0) return;
                luminance = ImageLuminance.CalculateBgraPremultiplied(pixels.ToArray(), raster.PixelWidth, raster.PixelHeight, raster.PixelWidth * 4);
            }
            if (generation != _generation || !IsLoaded) return;
            _luminance = luminance;
            _measuredGeneration = generation;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Could not measure icon luminance: {error.Message}");
        }
        finally
        {
            if (_measuringGeneration == generation) _measuringGeneration = -1;
        }
        if (generation == _generation && IsLoaded) UpdateBackground();
    }

    private void UpdateBackground()
    {
        var dark = ActualTheme == ElementTheme.Dark;
        var show = _image.ActualHeight < ActualHeight ||
            _luminance == (dark ? ImageLuminanceType.Dark : ImageLuminanceType.Bright);
        var style = show ? _backgroundStyle : null;
        if (!ReferenceEquals(_border.Style, style)) _border.Style = style;
    }
}
