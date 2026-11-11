using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using WinNewsWire.Core;

namespace WinNewsWire.Services;

internal static class SharedFeedDataReader
{
    public static async Task<string> ReadAsync(DataPackageView data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Contains(StandardDataFormats.WebLink))
            return SharedFeedUrl.Parse((await data.GetWebLinkAsync()).OriginalString);
#pragma warning disable CS0618
        if (data.Contains(StandardDataFormats.Uri))
            return SharedFeedUrl.Parse((await data.GetUriAsync()).OriginalString);
#pragma warning restore CS0618
        if (data.Properties.ContentSourceWebLink is { } source)
            return SharedFeedUrl.Parse(source.OriginalString);
        if (data.Contains(StandardDataFormats.Text))
            return SharedFeedUrl.Parse(await data.GetTextAsync());
        throw new ArgumentException("The shared data does not contain a feed or website URL.");
    }
}
