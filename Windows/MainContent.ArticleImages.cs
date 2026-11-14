using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using WinNewsWire.AppShared.ArticleRendering;

namespace WinNewsWire;

public sealed partial class MainContent
{
    private async Task ServeGrayscaleArticleIconAsync(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args,
        string path, long revision, Windows.Foundation.Deferral deferral, ulong navigation)
    {
        bool IsCurrent() => !_shuttingDown && navigation == _articleNavigationId;
        try
        {
            var bytes = await AppRuntime.AppService.Shared.GrayscaleIconImages.DataForAsync(path, revision, _thumbnailLifetime.Token);
            if (IsCurrent()) SetWebResource(sender, args, bytes, "image/png");
        }
        catch (OperationCanceledException) when (_shuttingDown || _thumbnailLifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        {
            App.LogException("Load grayscale article icon", error);
            if (IsCurrent()) SetWebResource(sender, args, [], "text/plain", 500);
        }
        finally { deferral.Complete(); }
    }

    private async Task ServeArticleImageAsync(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args,
        Uri requestUrl, Windows.Foundation.Deferral deferral, ulong navigation)
    {
        bool IsCurrent() => !_shuttingDown && navigation == _articleNavigationId;
        try
        {
            if (!IsCurrent()) return;
            if (!ArticleImageResource.TryGetImageUrl(requestUrl, out var originalUrl))
            {
                SetWebResource(sender, args, [], "text/plain", 400);
                return;
            }
            var service = _thumbnailService ?? throw new InvalidOperationException("The article image service is not initialized.");
            var image = await service.FullSizeImageAsync(originalUrl, _thumbnailLifetime.Token);
            if (!IsCurrent()) return;
            if (image is { } source) SetWebResource(sender, args, source.Data, source.MimeType);
            else SetWebResource(sender, args, [], "text/plain", 404);
        }
        catch (OperationCanceledException) when (_shuttingDown || _thumbnailLifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or Microsoft.Data.Sqlite.SqliteException)
        {
            App.LogException("Load shared article image", error);
            if (IsCurrent()) SetWebResource(sender, args, [], "text/plain", 500);
        }
        finally { deferral.Complete(); }
    }
}
