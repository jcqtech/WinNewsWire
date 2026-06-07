using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.Email;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace WinNewsWire.AppWindows;

/// <summary>
/// Modal-ish feedback window. Mirrors the "Report a Bug" flow common to NetNewsWire
/// (which uses the system Mail compose with a prefilled subject/body) but adds an
/// in-app form so users on Windows can submit thumbs-up/down with optional screenshot
/// and debug-state attachments without opening an email client first.
/// </summary>
public sealed partial class FeedbackWindow : Window
{
    public const string FeedbackEmailAddress = "feedback@winnewswire.com";

    // Replace with the real Microsoft Store ProductId once the app is published.
    // The deep link `ms-windows-store://review/?ProductId={id}` opens the rating
    // dialog directly; the https URL is the website fallback.
    private const string StoreProductId = "9NWINNEWSWIRE";
    private const string StoreRatingDeepLink = "ms-windows-store://review/?ProductId=" + StoreProductId;
    private const string StoreWebsiteUrl = "https://apps.microsoft.com/detail/" + StoreProductId;

    private readonly SoftwareBitmap? _screenshot;
    private readonly string _debugInfo;

    private bool? _thumbsUp;   // null = nothing selected, true = up, false = down

    public FeedbackWindow(SoftwareBitmap? screenshot, string debugInfo)
    {
        InitializeComponent();
        WindowIconHelper.ApplyFlatIcon(this);
        WindowThemeHelper.Attach(this);
        ResizeToContent();

        _screenshot = screenshot;
        _debugInfo = debugInfo;

        IncludeScreenshotCheckBox.IsEnabled = _screenshot is not null;
        IncludeScreenshotCheckBox.IsChecked = _screenshot is not null;
        DebugInfoText.Text = _debugInfo;

        UpdateSendEnabled();
    }

    private void ResizeToContent()
    {
        try
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            appWindow.Resize(new Windows.Graphics.SizeInt32(560, 720));
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"FeedbackWindow resize: {ex.Message}"); }
    }

    private void ThumbsUp_Click(object sender, RoutedEventArgs e)
    {
        _thumbsUp = true;
        ThumbsUpButton.IsChecked = true;
        ThumbsDownButton.IsChecked = false;
        UpdateSendEnabled();
    }

    private void ThumbsDown_Click(object sender, RoutedEventArgs e)
    {
        _thumbsUp = false;
        ThumbsDownButton.IsChecked = true;
        ThumbsUpButton.IsChecked = false;
        UpdateSendEnabled();
    }

    private void ContactMe_Changed(object sender, RoutedEventArgs e)
    {
        EmailRow.Visibility = ContactMeCheckBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdateSendEnabled();
    }

    private void EmailBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateSendEnabled();
    private void FeedbackBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateSendEnabled();

    private void UpdateSendEnabled()
    {
        bool hasRating = _thumbsUp.HasValue;
        bool hasText = !string.IsNullOrWhiteSpace(FeedbackBox.Text);
        bool emailOk = ContactMeCheckBox.IsChecked != true
                       || IsPlausibleEmail(EmailBox.Text);
        SendButton.IsEnabled = hasRating && hasText && emailOk;
    }

    private static bool IsPlausibleEmail(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var at = s.IndexOf('@');
        return at > 0 && at < s.Length - 1 && s.IndexOf('.', at) > at;
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        SendButton.IsEnabled = false;
        try
        {
            await ComposeAndSendAsync();
            ShowConfirmation();
        }
        catch (Exception ex)
        {
            ErrorBar.Title = "Couldn't open your email client";
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
            SendButton.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private async Task ComposeAndSendAsync()
    {
        var rating = _thumbsUp == true ? "\U0001F44D Thumbs up" : "\U0001F44E Thumbs down";
        var subject = $"WinNewsWire Feedback — {(_thumbsUp == true ? "👍" : "👎")}";

        var body = new StringBuilder();
        body.AppendLine($"Rating: {rating}");
        body.AppendLine();
        body.AppendLine("Feedback:");
        body.AppendLine(FeedbackBox.Text.Trim());
        body.AppendLine();

        if (ContactMeCheckBox.IsChecked == true && !string.IsNullOrWhiteSpace(EmailBox.Text))
        {
            body.AppendLine($"Reply to: {EmailBox.Text.Trim()}");
            body.AppendLine();
        }

        if (IncludeDebugCheckBox.IsChecked == true)
        {
            body.AppendLine("----- Debug Info -----");
            body.AppendLine(_debugInfo);
            body.AppendLine("----------------------");
        }

        var msg = new EmailMessage
        {
            Subject = subject,
            Body = body.ToString(),
        };
        msg.To.Add(new EmailRecipient(FeedbackEmailAddress));

        if (ContactMeCheckBox.IsChecked == true && IsPlausibleEmail(EmailBox.Text))
        {
            try { msg.Sender = new EmailRecipient(EmailBox.Text.Trim()); } catch { }
        }

        // Attach screenshot when present + opted in. EmailMessage.Attachments
        // accepts any RandomAccessStreamReference; we encode the SoftwareBitmap
        // as PNG into an in-memory stream and hand it over.
        if (IncludeScreenshotCheckBox.IsChecked == true && _screenshot is not null)
        {
            try
            {
                var pngRef = await EncodeBitmapToPngStreamAsync(_screenshot);
                msg.Attachments.Add(new EmailAttachment("WinNewsWire.png", pngRef));
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Screenshot attach failed: {ex}"); }
        }

        await EmailManager.ShowComposeNewEmailAsync(msg);
    }

    private static async Task<RandomAccessStreamReference> EncodeBitmapToPngStreamAsync(SoftwareBitmap bitmap)
    {
        var stream = new InMemoryRandomAccessStream();
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
            Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);

        // Encoder requires Bgra8 + Premultiplied; convert if needed.
        SoftwareBitmap toEncode = bitmap;
        if (bitmap.BitmapPixelFormat != Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8
            || bitmap.BitmapAlphaMode != Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied)
        {
            toEncode = SoftwareBitmap.Convert(bitmap,
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
        }

        encoder.SetSoftwareBitmap(toEncode);
        await encoder.FlushAsync();
        stream.Seek(0);
        return RandomAccessStreamReference.CreateFromStream(stream);
    }

    private void ShowConfirmation()
    {
        FormPanel.Visibility = Visibility.Collapsed;
        ConfirmationPanel.Visibility = Visibility.Visible;
        // Only prompt for a Store rating when the user said they're happy.
        StoreRatingPanel.Visibility = _thumbsUp == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void RateInStore_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var ok = await Windows.System.Launcher.LaunchUriAsync(new Uri(StoreRatingDeepLink));
            if (!ok)
                await Windows.System.Launcher.LaunchUriAsync(new Uri(StoreWebsiteUrl));
        }
        catch
        {
            try { await Windows.System.Launcher.LaunchUriAsync(new Uri(StoreWebsiteUrl)); } catch { }
        }
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();
}

/// <summary>Collects an opt-in debug payload describing the runtime state of the
/// app. Pure read of in-memory data — no PII beyond account names. Surfaced in
/// the feedback window so the user always sees exactly what would be sent.</summary>
public static class FeedbackDebugCollector
{
    public static string Collect()
    {
        var sb = new StringBuilder();
        var asm = System.Reflection.Assembly.GetEntryAssembly()
                  ?? System.Reflection.Assembly.GetExecutingAssembly();

        sb.AppendLine($"App: WinNewsWire {asm.GetName().Version}");
        sb.AppendLine($"OS: {Environment.OSVersion} ({(Environment.Is64BitOperatingSystem ? "x64" : "x86")})");
        sb.AppendLine($"Runtime: .NET {Environment.Version}");
        sb.AppendLine($"CPU count: {Environment.ProcessorCount}");
        sb.AppendLine($"Locale: {System.Globalization.CultureInfo.CurrentUICulture}");

        try
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            sb.AppendLine($"Working set: {p.WorkingSet64 / (1024 * 1024)} MB");
            sb.AppendLine($"Process uptime: {(DateTime.Now - p.StartTime):c}");
        }
        catch { }

        try
        {
            sb.AppendLine($"Appearance: {WinNewsWire.Core.AppDefaults.Shared.AppearanceMode}");
            sb.AppendLine($"Reader-mode default: {WinNewsWire.Core.AppDefaults.Shared.ReaderModeDefault}");
            sb.AppendLine($"Unified layout: {WinNewsWire.Core.AppDefaults.Shared.UnifiedLayout}");
            sb.AppendLine($"Refresh interval: {(WinNewsWire.AppShared.Timer.RefreshInterval)WinNewsWire.Core.AppDefaults.Shared.RefreshIntervalRaw}");
            sb.AppendLine($"Article text size: {(WinNewsWire.Core.AppDefaults.FontSize)WinNewsWire.Core.AppDefaults.Shared.ArticleTextSizeRaw}");
            sb.AppendLine($"Article theme: {WinNewsWire.Core.AppDefaults.Shared.CurrentThemeName}");
        }
        catch { }

        try
        {
            var accounts = WinNewsWire.AppRuntime.AppService.Shared.Accounts.Accounts.ToList();
            sb.AppendLine();
            sb.AppendLine($"Accounts: {accounts.Count}");
            foreach (var a in accounts)
            {
                var feeds = a.FlattenedFeeds().ToList();
                var unread = feeds.Sum(f => f.UnreadCount);
                sb.AppendLine($"  - {a.Name} ({a.Type}): {feeds.Count} feeds, {unread} unread, active={a.IsActive}");
            }
        }
        catch (Exception ex) { sb.AppendLine($"Accounts: <unavailable: {ex.Message}>"); }

        try
        {
            var recent = WinNewsWire.AppRuntime.AppService.Shared.ErrorLog
                .FetchRecentAsync(limit: 5).GetAwaiter().GetResult();
            if (recent.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Recent errors:");
                foreach (var e in recent)
                {
                    var detail = e.ErrorMessage?.Replace("\r", "").Replace("\n", " \u23CE ");
                    if (detail is { Length: > 240 }) detail = detail[..240] + "\u2026";
                    sb.AppendLine($"  [{e.Date:s}] {e.Operation}: {detail}");
                }
            }
        }
        catch { }

        return sb.ToString();
    }
}
