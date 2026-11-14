#if PARITY_PROBE
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using WinNewsWire.AppShared;
using WinNewsWire.Core;
using WinNewsWire.Helpers;

namespace WinNewsWire.Diagnostics;

internal static partial class ParityProbe
{
    private static async Task<object?> CheckLiveGrayscaleIconsAsync(MainWindow window)
    {
        bool saved = AppDefaults.Shared.GetGrayscaleIcons();
        string path = Path.Combine(OutputDirectory, "live-grayscale.bmp");
        var color = ThumbnailBitmap(64, 32);
        File.WriteAllBytes(path, color);
        var gray = WindowsImage.GrayscaleImageData(color);
        try
        {
            AppDefaults.Shared.SetGrayscaleIcons(false);
            return await WithAppearanceTimelineAsync(window, async (content, list, rows, _) =>
            {
                var sidebar = new Models.SidebarItem { Title = "Grayscale sidebar fixture", FaviconPath = path };
                content.ViewModel.SidebarItems.Add(sidebar);
                var sidebarImage = new Image { Width = 48, Height = 48 };
                sidebarImage.SetBinding(Image.SourceProperty, new Binding
                {
                    Source = sidebar, Path = new PropertyPath(nameof(Models.SidebarItem.FaviconSource)),
                    Mode = BindingMode.OneWay, Converter = new Converters.PathToImageSourceConverter()
                });
                var host = Descendants(content).OfType<Grid>().First();
                host.Children.Add(sidebarImage);
                var responses = new ConcurrentDictionary<string, byte[]>();
                var failures = new ConcurrentQueue<Exception>();
                async void Response(CoreWebView2 sender, CoreWebView2WebResourceResponseReceivedEventArgs args)
                {
                    if (!args.Request.Uri.StartsWith(ViewModels.MainViewModel.ArticleIconUrl, StringComparison.Ordinal)) return;
                    try
                    {
                        using var stream = await args.Response.GetContentAsync();
                        using var reader = stream.AsStreamForRead();
                        using var copy = new MemoryStream();
                        await reader.CopyToAsync(copy);
                        responses[args.Request.Uri] = copy.ToArray();
                    }
                    catch (Exception error) { failures.Enqueue(error); }
                }
                var core = content.ProbeWebView.CoreWebView2;
                core.WebResourceResponseReceived += Response;
                try
                {
                    rows[0] = AppearanceRow("live-gray", "Live grayscale", string.Concat(Enumerable.Repeat("<p>Reader content stays intact.</p>", 100)));
                    rows[0].FaviconPath = path;
                    content.ViewModel.SelectedArticle = rows[0];
                    IconView Icon() => Descendants((DependencyObject)list.ContainerFromIndex(0)).OfType<IconView>().Single();
                    await WaitFor(() => Task.FromResult(content.ProbeArticleReady && Icon().ImageSource is BitmapImage { PixelWidth: 64 }
                        && sidebarImage.Source is BitmapImage { PixelWidth: 64 }), "Visible color icons");
                    await WaitFor(() => ScriptBool(content, "document.getElementById('nnwImageIcon')?.naturalWidth === 64"),
                        "Reader color icon loaded");
                    var colorSource = Icon().ImageSource;
                    await content.ProbeWebView.ExecuteScriptAsync("window.liveGraySentinel = true; window.scrollTo(0, 250);");
                    string readerPosition = await content.ProbeWebView.ExecuteScriptAsync("window.scrollY");
                    AppDefaults.Shared.SetGrayscaleIcons(true);
                    await SettleAppearanceAsync(window, list);
                    Require(!ReferenceEquals(colorSource, Icon().ImageSource), "Visible timeline icons did not update when grayscale changed.");
                    await WaitFor(() => Task.FromResult(Icon().ImageSource is BitmapImage { PixelWidth: 64 }
                        && ReferenceEquals(sidebarImage.Source, Icon().ImageSource)), "Sidebar and timeline share updated grayscale source");
                    var graySource = Icon().ImageSource;
                    await WaitFor(() => Task.FromResult(responses.Any(pair => pair.Key.Contains("grayscale=1", StringComparison.Ordinal)
                        && pair.Value.SequenceEqual(gray))), "Reader header receives grayscale image bytes");
                    Require(await ScriptBool(content, "window.liveGraySentinel === true && Math.abs(window.scrollY - " + readerPosition + ") < 2"),
                        "Updating the reader icon reloaded its document or moved its reading position.");
                    responses.Clear();
                    await Task.Run(() => AppDefaults.Shared.SetGrayscaleIcons(false));
                    await WaitFor(() => Task.FromResult(Icon().ImageSource is BitmapImage { UriSource: not null }
                        && sidebarImage.Source is BitmapImage { UriSource: not null }
                        && responses.Any(pair => pair.Key.Contains("grayscale=0", StringComparison.Ordinal) && pair.Value.SequenceEqual(color))),
                        "Color icons and reader header restored");
                    AppDefaults.Shared.SetGrayscaleIcons(true);
                    await WaitFor(() => Task.FromResult(ReferenceEquals(graySource, Icon().ImageSource)
                        && ReferenceEquals(graySource, sidebarImage.Source)), "Cached grayscale variant reused");
                    Require(rows[0].FaviconSource.Revision == 0 && sidebar.FaviconSource.Revision == 0,
                        "Appearance changes invalidated the source image revision.");
                    Require(failures.IsEmpty, "Reader icon response capture failed: " + string.Join("; ", failures.Select(error => error.Message)));
                    return new { Sidebar = true, Timeline = true, ReaderBytes = true, DocumentPreserved = true, VariantReused = true };
                }
                finally
                {
                    core.WebResourceResponseReceived -= Response;
                    host.Children.Remove(sidebarImage);
                    content.ViewModel.SidebarItems.Remove(sidebar);
                }
            });
        }
        finally { AppDefaults.Shared.SetGrayscaleIcons(saved); }
    }
}
#endif
