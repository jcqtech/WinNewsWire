using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using WinNewsWire.AppShared.ArticleThemes;

namespace WinNewsWire;

public sealed partial class MainContent
{
    private async Task ServeThemeResourceAsync(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args,
        Uri url, Windows.Foundation.Deferral deferral, ulong navigation)
    {
        string? folder = _renderedThemeFolder;
        string? host = _renderedThemeHost;
        string style = _renderedThemeStyle;
        bool IsCurrent() => !_shuttingDown && navigation == _articleNavigationId
            && folder == _renderedThemeFolder && host == _renderedThemeHost;
        try
        {
            if (!IsCurrent()) return;
            if (folder is null || (url.Host != host && url.Host != ViewModels.MainViewModel.ThemeResourceHost))
            {
                SetWebResource(sender, args, [], "text/plain", 404);
                return;
            }
            if (url.AbsolutePath.Equals(ViewModels.MainViewModel.ThemeStylesheetPath, StringComparison.OrdinalIgnoreCase))
            {
                SetWebResource(sender, args, Encoding.UTF8.GetBytes(style), "text/css; charset=utf-8", allowCrossOrigin: true);
                return;
            }
            var resource = await ArticleThemeResource.ReadAsync(folder, url.AbsolutePath, _thumbnailLifetime.Token);
            if (IsCurrent()) SetWebResource(sender, args, resource.Data, resource.ContentType, allowCrossOrigin: true);
        }
        catch (OperationCanceledException) when (_shuttingDown || _thumbnailLifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            if (IsCurrent()) SetWebResource(sender, args, [], "text/plain", 404);
        }
        catch (InvalidDataException error)
        {
            App.LogException("Load article theme resource", error);
            if (IsCurrent()) SetWebResource(sender, args, [], "text/plain", 400);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            App.LogException("Load article theme resource", error);
            if (IsCurrent()) SetWebResource(sender, args, [], "text/plain", 500);
        }
        finally { deferral.Complete(); }
    }
}
