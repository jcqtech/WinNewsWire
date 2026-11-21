#if PARITY_PROBE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinNewsWire.AppShared;
using WinNewsWire.Core;

namespace WinNewsWire.Diagnostics;

internal static partial class ParityProbe
{
    private static async Task<object?> CheckPreferencesLayoutAsync(MainWindow window)
    {
        var preferences = new AppWindows.PreferencesWindow();
        var root = (FrameworkElement)preferences.Content;
        var samples = new List<object>();
        var failures = new List<string>();
        var savedFeedIcons = AppDefaults.Shared.GetTimelineFeedIconsDisplay();
        var controls = new[]
        {
            "UnreadCountDisplayCombo", "RefreshIntervalCombo", "TimelineThumbnailsCombo", "TimelineIconSizeCombo",
            "TimelineTextSizeCombo", "TimelineNumberOfLinesCombo", "TimelineShowSeparatorsToggle",
            "TimelineShowFullTitlesToggle", "TimelineFadeReadArticlesToggle", "TimelineFeedNameAndDateAboveTitleToggle",
            "AppearanceModeCombo", "GrayscaleIconsToggle", "SidebarShowIconsToggle", "SidebarTextSizeCombo",
            "ShowTodayFeedToggle", "ShowUnreadFeedToggle", "ShowStarredFeedToggle", "TextSizeCombo", "ThemeCombo", "OpenBackgroundToggle"
        };
        T Element<T>(string name) where T : FrameworkElement
            => Descendants(root).OfType<T>().Single(item => item.Name == name);
        Border Card(FrameworkElement element)
        {
            var style = (Style)root.Resources["SettingsCardStyle"];
            for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
                if (current is Border border && ReferenceEquals(border.Style, style)) return border;
            throw new InvalidOperationException("The preferences input has no settings card: " + element.Name);
        }
        global::Windows.Foundation.Rect Bounds(FrameworkElement element) => element.TransformToVisual(root)
            .TransformBounds(new(0, 0, element.ActualWidth, element.ActualHeight));
        async Task Capture(FrameworkElement element, string name)
        {
            var image = new RenderTargetBitmap();
            await image.RenderAsync(element);
            byte[] pixels = (await image.GetPixelsAsync()).ToArray();
            // Flatten the transparent card onto a themed backing so dark-mode captures stay readable.
            byte background = element.ActualTheme == ElementTheme.Dark ? (byte)32 : (byte)243;
            for (int pixel = 0; pixel < pixels.Length; pixel += 4)
            {
                int remaining = 255 - pixels[pixel + 3];
                for (int channel = 0; channel < 3; channel++)
                    pixels[pixel + channel] = (byte)Math.Min(255, pixels[pixel + channel] + (background * remaining + 127) / 255);
                pixels[pixel + 3] = 255;
            }
            using var file = File.Create(Path.Combine(OutputDirectory, name + ".png"));
            using var stream = file.AsRandomAccessStream();
            var encoder = await global::Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                global::Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                (uint)image.PixelWidth, (uint)image.PixelHeight, 96, 96, pixels);
            await encoder.FlushAsync();
        }
        try
        {
            preferences.Activate();
            await WaitFor(() => Task.FromResult(root.IsLoaded), "Preferences layout fixture");
            foreach (double width in new[] { 1280d, 640d })
            {
                preferences.AppWindow.Resize(new((int)Math.Ceiling(width * root.XamlRoot.RasterizationScale),
                    (int)Math.Ceiling(850 * root.XamlRoot.RasterizationScale)));
                root.RequestedTheme = ElementTheme.Light;
                await WaitForRenderedFramesAsync();
                root.UpdateLayout();
                double referenceHeight = Card(Element<ComboBox>("RefreshIntervalCombo")).ActualHeight;
                foreach (string name in controls)
                {
                    var input = Element<Control>(name);
                    var card = Card(input);
                    DependencyObject? parent = VisualTreeHelper.GetParent(input);
                    while (parent is not null && parent is not Grid) parent = VisualTreeHelper.GetParent(parent);
                    var row = parent as Grid ?? throw new InvalidOperationException("The preferences input has no row.");
                    var label = row.Children.OfType<TextBlock>().First();
                    var box = Bounds(card);
                    samples.Add(new
                    {
                        Width = width, Name = name, Card = box, card.Padding, card.CornerRadius,
                        Input = Bounds(input), InputHeight = input.ActualHeight, InputAlignment = input.VerticalAlignment.ToString(),
                        Label = Bounds(label), label.Text, ReferenceHeight = referenceHeight
                    });
                    if (width > 1000 && Math.Abs(card.ActualHeight - referenceHeight) > 1)
                        failures.Add(name + ": one-line card height differs from dropdown rows.");
                    if (Bounds(input).Top < box.Top + card.Padding.Top - 1
                        || Bounds(input).Bottom > box.Bottom - card.Padding.Bottom + 1
                        || Bounds(input).Right > box.Right - card.Padding.Right + 1)
                        failures.Add(name + ": the input is clipped by its parent card.");
                    if (Math.Abs(Bounds(input).Top + input.ActualHeight / 2 - Bounds(label).Top - label.ActualHeight / 2) > 1)
                        failures.Add(name + ": the label and input are not vertically centered.");
                }
                foreach (string name in new[] { "UnreadCountsStatus", "TimelineThumbnailsStatus", "TimelineFeedIconsStatus", "AppearanceStatus" })
                {
                    var text = Element<TextBlock>(name);
                    if (string.IsNullOrWhiteSpace(text.Text) && text.Visibility != Visibility.Collapsed)
                        failures.Add(name + ": an empty message still reserves space.");
                }
                var heading = (FrameworkElement)Element<Expander>("TimelinePane").Header;
                var icon = Descendants(heading).OfType<IconElement>().Single();
                var glyph = Descendants(icon).OfType<TextBlock>().FirstOrDefault();
                samples.Add(new { Width = width, TimelineIcon = icon.GetType().Name, icon.ActualWidth, icon.ActualHeight,
                    GlyphSize = glyph?.FontSize, GlyphDesired = glyph?.DesiredSize });
                if (icon is not FontIcon font || font.FontSize != 16)
                    failures.Add("Timeline icon: the glyph is not consistently sized with the other settings icons.");
                if (width > 1000)
                {
                    await Capture(Card(Element<ComboBox>("UnreadCountDisplayCombo")), "preferences-unread");
                    await Capture(Card(Element<ComboBox>("TimelineThumbnailsCombo")), "preferences-thumbnails");
                    await Capture(Card(Element<ToggleSwitch>("TimelineShowSeparatorsToggle")), "preferences-toggle");
                    var panel = (Panel)heading;
                    var background = panel.Background;
                    try
                    {
                        panel.Background = new SolidColorBrush(Microsoft.UI.Colors.White);
                        await Capture(heading, "preferences-timeline-heading");
                    }
                    finally { panel.Background = background; }
                }
            }
            File.WriteAllText(Path.Combine(OutputDirectory, "preferences-layout.json"),
                JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
            Require(failures.Count == 0, string.Join(Environment.NewLine, failures.Distinct()));

            foreach (var (messageName, inputName) in new[]
            {
                ("UnreadCountsStatus", "UnreadCountDisplayCombo"),
                ("TimelineThumbnailsStatus", "TimelineThumbnailsCombo")
            })
            {
                var message = Element<TextBlock>(messageName);
                var card = Card(Element<ComboBox>(inputName));
                double height = card.ActualHeight;
                string text = message.Text;
                try
                {
                    message.Text = string.Concat(Enumerable.Repeat("A detailed preference error must remain visible and wrap inside its card. ", 4));
                    await WaitFor(() => Task.FromResult(message.Visibility == Visibility.Visible && card.ActualHeight > height + 10),
                        "Visible error grows its preference card");
                    Require(Bounds(message).Bottom <= Bounds(card).Bottom - card.Padding.Bottom + 1
                        && Bounds(message).Right <= Bounds(card).Right - card.Padding.Right + 1,
                        "The expanded preference error is clipped.");
                    message.Text = "";
                    await WaitFor(() => Task.FromResult(message.Visibility == Visibility.Collapsed
                        && Math.Abs(card.ActualHeight - height) < 1), "Cleared error returns its row to the normal height");
                }
                finally { message.Text = text; }
            }
            var icons = Element<ComboBox>("TimelineFeedIconsCombo");
            var description = Element<TextBlock>("TimelineFeedIconsDescription");
            icons.SelectedItem = icons.Items.OfType<ComboBoxItem>()
                .Single(item => item.Tag is TimelineFeedIconsDisplay.Always);
            await WaitFor(() => Task.FromResult(description.Visibility == Visibility.Collapsed
                && Math.Abs(Card(icons).ActualHeight - Card(Element<ComboBox>("TimelineThumbnailsCombo")).ActualHeight) < 1),
                "Empty feed-icon explanation does not leave a blank row");
            icons.SelectedItem = icons.Items.OfType<ComboBoxItem>()
                .Single(item => item.Tag is TimelineFeedIconsDisplay.Automatic);
            await WaitFor(() => Task.FromResult(description.Visibility == Visibility.Visible
                && Card(icons).ActualHeight > Card(Element<ComboBox>("TimelineThumbnailsCombo")).ActualHeight),
                "Automatic feed-icon explanation remains visible");
            foreach (string name in new[] { "TimelineTextSizeCombo", "TimelineShowSeparatorsToggle" })
            {
                var input = Element<Control>(name);
                var card = Card(input);
                var label = ((Grid)VisualTreeHelper.GetParent(input)).Children.OfType<TextBlock>().First();
                double height = card.ActualHeight, inputHeight = input.ActualHeight, font = label.FontSize;
                string text = label.Text;
                try
                {
                    label.FontSize = 24;
                    label.Text = string.Join(" ", Enumerable.Repeat(text, 4));
                    await WaitFor(() => Task.FromResult(card.ActualHeight > height && label.ActualHeight > 40),
                        "Wrapped large labels grow preference cards naturally");
                    Require(Bounds(label).Bottom <= Bounds(card).Bottom - card.Padding.Bottom + 1
                        && Math.Abs(input.ActualHeight - inputHeight) < 1
                        && Math.Abs(Bounds(input).Top + input.ActualHeight / 2 - Bounds(label).Top - label.ActualHeight / 2) < 1,
                        "A large label clips the card or stretches its input control.");
                }
                finally { label.Text = text; label.FontSize = font; }
            }
            Element<Expander>("AdvancedOptionsExpander").IsExpanded = true;
            await WaitForRenderedFramesAsync();
            foreach (string name in new[] { "ReaderModeDefaultToggle", "JavascriptToggle", "DebugMenuToggle" })
            {
                var input = Element<ToggleSwitch>(name);
                var row = (Grid)VisualTreeHelper.GetParent(input);
                Require(row.ActualHeight >= 64 && Bounds(input).Bottom <= Bounds(row).Bottom - row.Padding.Bottom + 1,
                    "An advanced preference row is undersized.");
            }
            root.RequestedTheme = ElementTheme.Dark;
            await WaitFor(() => Task.FromResult(root.ActualTheme == ElementTheme.Dark
                && Element<ComboBox>("TimelineThumbnailsCombo").ActualTheme == ElementTheme.Dark),
                "Preferences controls inherit the dark appearance");
            await WaitForRenderedFramesAsync();
            await Capture(Card(Element<ComboBox>("UnreadCountDisplayCombo")), "preferences-unread-dark");
            await Capture(Card(Element<ComboBox>("TimelineThumbnailsCombo")), "preferences-thumbnails-dark");
            return new { ConsistentSingleRows = true, CompactEmptyMessages = true, CenteredControls = true,
                WideAndNarrow = true, TimelineIcon = true, RenderedCards = true, DynamicMessages = true,
                DescriptionsPreserved = true, LargeWrappedLabels = true, AdvancedRows = true, LightAndDark = true };
        }
        finally
        {
            preferences.Close();
            AppDefaults.Shared.SetTimelineFeedIconsDisplay(savedFeedIcons);
            window.Activate();
        }
    }
}
#endif
