#if PARITY_PROBE
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinNewsWire.Account;
using WinNewsWire.AppShared;
using WinNewsWire.AppShared.ArticleThemes;
using WinNewsWire.Articles;
using WinNewsWire.Core;

namespace WinNewsWire.Diagnostics;

internal static partial class ParityProbe
{
    private static readonly string[] BundledThemeNames =
    [
        "Default", "Appanoose", "Biblioteca", "Gray", "Hyperlegible", "NewsFax",
        "Plum", "Promenade", "Sepia", "Tiqoe Dark", "Verdana Revival"
    ];

    private static string ThemeMenuName(ToggleMenuFlyoutItem item)
    {
        string suffix = AppStrings.Get("ToolbarBuiltInTheme");
        return item.Text.EndsWith(suffix, StringComparison.Ordinal) ? item.Text[..^suffix.Length] : item.Text;
    }

    private static async Task<object?> CheckThemeParityAsync(MainWindow window, string serverUrl)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var manager = ArticleThemesManager.Shared;
        var defaults = AppDefaults.Shared;
        var originalState = window.CaptureState();
        string originalTheme = manager.CurrentTheme.Name;
        int originalSize = defaults.ArticleTextSizeRaw;
        var originalAppearance = defaults.AppearanceMode;
        bool originalScripts = defaults.ArticleContentJavascriptEnabled;
        bool originalStatus = defaults.ShowStatusBar, originalStatusThemes = defaults.ShowStatusBarThemeMenu;
        var account = AccountManager.Shared.DefaultAccount!;
        var feed = account.AddFeed(serverUrl + "/theme-fixture", "Theme Publisher");
        feed.HomePageUrl = serverUrl + "/";
        feed.FaviconUrl = serverUrl + "/icon.png";
        const string id = "theme-parity-article";
        var video = Attachment.Create(null, serverUrl + "/theme-video.mp4", "video/mp4", null, null, null)!;
        var audio = Attachment.Create(null, serverUrl + "/theme-audio.mp3", "audio/mpeg", null, null, null)!;
        var article = new Article(account.AccountID, id, feed.FeedID, id, "Theme <b>parity</b> fixture",
            "<p id='theme-body-marker'>Theme body text.</p><blockquote>A quotation</blockquote>"
            + "<pre>Code sample</pre><table id='theme-body-table'><tr><td>Table cell</td></tr></table>"
            + string.Concat(Enumerable.Repeat("<p>A paragraph for checking the reading layout.</p>", 25)),
            null, null, serverUrl + "/article", null, null, null, DateTime.UtcNow, null, null, [video, audio],
            new ArticleStatus(id, true, false, DateTime.UtcNow));
        var customFolders = new List<string>();
        var samples = new List<object>();
        AppWindows.PreferencesWindow? preferences = null;
        MainWindow? companion = null;
        MenuFlyout? themeMenu = null;
        try
        {
            Require(manager.ThemeNames.SequenceEqual(BundledThemeNames),
                "The deployed application does not discover all eleven canonical themes in menu order.");
            await account.Database.UpdateArticlesAsync([article]);
            defaults.ArticleContentJavascriptEnabled = false;
            defaults.ShowStatusBar = defaults.ShowStatusBarThemeMenu = true;
            model.BuildSidebar();
            await model.ProbeDrainViewUpdatesAsync();
            model.SelectedSidebarItem = model.FindSidebarItemForFeed(account.AccountID, feed.FeedID);
            await model.FilterArticlesAsync();
            await model.WaitForCurrentTimelineAsync(CancellationToken.None);
            model.SelectedArticle = model.ArticleItems.Single(item => item.Id == id);
            await WaitFor(() => Task.FromResult(content.ProbeArticleReady), "Theme fixture article");
            companion = App.OpenMainWindow(window.CaptureState());
            await companion.ContentInitialization;
            await companion.Restoration;
            await WaitFor(() => Task.FromResult(companion.ProbeContent.ProbeArticleReady), "Companion themed reader");

            preferences = new AppWindows.PreferencesWindow();
            preferences.Activate();
            await WaitFor(() => Task.FromResult(preferences.Content is FrameworkElement { IsLoaded: true }), "Theme preferences");
            foreach (var expander in Descendants(preferences.Content).OfType<Expander>()) expander.IsExpanded = true;
            var combo = (ComboBox)((FrameworkElement)preferences.Content).FindName("ThemeCombo");
            combo.StartBringIntoView();
            await WaitFor(() => Task.FromResult(combo.IsLoaded), "Visible theme picker");
            Require(combo.Items.OfType<ComboBoxItem>().Select(item => (string)item.Tag).SequenceEqual(BundledThemeNames),
                "Preferences did not expose every bundled theme.");
            var menuButton = (Button)content.FindName("StatusBarThemeButton");
            themeMenu = (MenuFlyout)menuButton.Flyout;
            var mainMenu = (MenuFlyoutSubItem)((FrameworkElement)window.Content).FindName("MenuThemes");

            foreach (string name in BundledThemeNames)
            {
                defaults.AppearanceMode = AppDefaults.Appearance.Light;
                defaults.ArticleTextSizeRaw = 1;
                content.ApplyWebViewColorScheme();
                bool changesTheme = manager.CurrentTheme.Name != name;
                if (changesTheme) await content.ProbeWebView.ExecuteScriptAsync("window.themeParityOldDocument = true;");
                themeMenu.ShowAt(menuButton);
                await WaitFor(() => Task.FromResult(themeMenu.Items.OfType<ToggleMenuFlyoutItem>().Count() == BundledThemeNames.Length
                    && themeMenu.Items.OfType<ToggleMenuFlyoutItem>().All(item => item.IsLoaded)),
                    "Native theme menu");
                Require(themeMenu.Items.OfType<ToggleMenuFlyoutItem>().Select(ThemeMenuName).SequenceEqual(BundledThemeNames)
                    && themeMenu.Items[1] is MenuFlyoutSeparator, "The native menu lost the Default separator or theme order.");
                var item = themeMenu.Items.OfType<ToggleMenuFlyoutItem>().Single(item => ThemeMenuName(item) == name);
                var peer = FrameworkElementAutomationPeer.FromElement(item) ?? FrameworkElementAutomationPeer.CreatePeerForElement(item);
                (peer?.GetPattern(PatternInterface.Toggle) as IToggleProvider
                    ?? throw new InvalidOperationException("The theme menu has no native Toggle pattern.")).Toggle();
                themeMenu.Hide();
                await WaitFor(async () => content.ProbeArticleReady && manager.CurrentTheme.Name == name
                    && (!changesTheme || await ScriptBool(content, "window.themeParityOldDocument !== true")), "Selected theme " + name);
                await WaitFor(() => Task.FromResult((combo.SelectedItem as ComboBoxItem)?.Tag as string == name
                    && mainMenu.Items.OfType<ToggleMenuFlyoutItem>().Single(item => item.IsChecked).Text.StartsWith(name, StringComparison.Ordinal)),
                    "Theme selection synchronized across controls");
                await WaitFor(async () => companion.ProbeContent.ProbeArticleReady
                    && companion.ProbeContent.ViewModel.CurrentRendering?.Style == model.CurrentRendering?.Style,
                    "Theme synchronized to companion reader");
                Require(await ScriptBool(content, """
                    (() => {
                        const body = document.getElementById('theme-body-marker');
                        const primary = document.querySelector('.nnw-primary-media video');
                        const enclosure = document.querySelector('audio');
                        return !!body && !!primary && !!enclosure
                            && !!(primary.compareDocumentPosition(body) & Node.DOCUMENT_POSITION_FOLLOWING)
                            && !!(body.compareDocumentPosition(enclosure) & Node.DOCUMENT_POSITION_FOLLOWING)
                            && document.getElementById('theme-body-table').parentElement.classList.contains('nnw-overflow')
                            && !document.body.innerHTML.includes('[[');
                    })()
                    """), name + " did not render its modern primary-media/body/enclosure slots.");
                if (manager.CurrentTheme.TemplateHtml.Contains("[[avatar_src]]", StringComparison.Ordinal))
                    Require(await ScriptBool(content, "document.getElementById('nnwImageIcon') !== null"),
                        name + " does not participate in live reader-icon updates.");
                var sizes = new List<double>();
                for (int size = 0; size <= 4; size++)
                {
                    defaults.ArticleTextSizeRaw = size;
                    string expectedClass = AppShared.ArticleRendering.ArticleRenderer.TextSizeClass(
                        (AppShared.ArticleRendering.ArticleTextSize)size);
                    await WaitFor(async () => content.ProbeArticleReady
                        && await ScriptBool(content, "document.querySelector("
                            + JsonSerializer.Serialize("." + expectedClass) + ") !== null"), name + " text-size class");
                    sizes.Add(double.Parse((await ScriptString(content,
                        "getComputedStyle(document.getElementById('theme-body-marker')).fontSize"))!.Replace("px", ""), CultureInfo.InvariantCulture));
                }
                Require(sizes.Zip(sizes.Skip(1), (a, b) => b > a).All(value => value),
                    name + " does not support all five increasing reader text sizes.");
                string font = name switch { "Hyperlegible" => "Atkinson Hyperlegible", "NewsFax" => "ModeSeven", _ => "" };
                if (font.Length > 0)
                    await WaitFor(() => ScriptBool(content, "Array.from(document.fonts).some(font => font.family.replaceAll('\"','').replaceAll(\"'\",'') === "
                        + JsonSerializer.Serialize(font) + " && font.status === 'loaded')"), name + " embedded font");
                if (name == "Gray")
                    Require((await ScriptString(content, "getComputedStyle(document.getElementById('nnwImageIcon')).filter"))!
                        .Contains("grayscale(1)", StringComparison.Ordinal), "The Gray theme lost its grayscale icon metadata.");
                defaults.AppearanceMode = AppDefaults.Appearance.Dark;
                content.ApplyWebViewColorScheme();
                await WaitFor(() => ScriptBool(content, "matchMedia('(prefers-color-scheme: dark)').matches"), name + " dark appearance");
                samples.Add(new { Theme = name, TextSizes = sizes, EmbeddedFont = font,
                    BodyColor = await ScriptString(content, "getComputedStyle(document.body).color"),
                    Background = await ScriptString(content, "getComputedStyle(document.body).backgroundColor") });
                File.WriteAllText(Path.Combine(OutputDirectory, "themes.json"), JsonSerializer.Serialize(samples,
                    new JsonSerializerOptions { WriteIndented = true }));
            }

            byte[] fontBytes = Convert.FromBase64String(Regex.Match(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
                "Themes", "Hyperlegible.nnwtheme", "stylesheet.css")), @"base64,([A-Za-z0-9+/=]+)").Groups[1].Value);
            string template = manager.Themes.Single(theme => theme.Name == "Default").TemplateHtml;
            foreach ((string name, int width) in new[] { ("Theme Asset A", 3), ("Theme Asset B", 7) })
            {
                string folder = Path.Combine(manager.FolderPath, name + ".nnwtheme");
                customFolders.Add(folder);
                Directory.CreateDirectory(Path.Combine(folder, "fonts"));
                File.WriteAllBytes(Path.Combine(folder, "fonts", "fixture.woff"), fontBytes);
                File.WriteAllText(Path.Combine(folder, "asset.svg"), $"<svg xmlns='http://www.w3.org/2000/svg' width='{width}' height='3'><rect width='{width}' height='3'/></svg>");
                File.WriteAllText(Path.Combine(folder, "Info.json"), """{"creatorName":"Theme test","version":1}""");
                File.WriteAllText(Path.Combine(folder, "template.html"), template);
                File.WriteAllText(Path.Combine(folder, "stylesheet.css"), """
                    @font-face { font-family: 'Local Theme Font'; src: url('fonts/fixture.woff') format('woff'); }
                    .articleBody { font-family: 'Local Theme Font', -apple-system-body; font-size: [[font-size]]px; color: rgb(11,22,33); }
                    body { background: white; }
                    """);
            }
            manager.Rescan();
            await WaitFor(() => Task.FromResult(combo.Items.OfType<ComboBoxItem>().Any(item => (string)item.Tag == "Theme Asset B")),
                "Live theme-list addition in existing preferences");
            string? firstHost = null;
            foreach ((string name, int width) in new[] { ("Theme Asset A", 3), ("Theme Asset B", 7) })
            {
                await content.ProbeWebView.ExecuteScriptAsync("window.themeParityOldDocument = true;");
                combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().Single(item => (string)item.Tag == name);
                await WaitFor(async () => content.ProbeArticleReady
                    && await ScriptBool(content, "window.themeParityOldDocument !== true")
                    && await ScriptString(content, "getComputedStyle(document.getElementById('theme-body-marker')).color") == "rgb(11, 22, 33)",
                    name + " custom stylesheet");
                Require(await ScriptString(content, "getComputedStyle(document.getElementById('theme-body-marker')).fontSize") == "22px",
                    "The file-backed stylesheet bypassed font-size macro processing.");
                await WaitFor(() => ScriptBool(content,
                    "Array.from(document.fonts).some(font => font.family.includes('Local Theme Font') && font.status === 'loaded')"),
                    name + " local cross-origin font");
                string host = (await ScriptString(content, "new URL(document.querySelector('link[rel=stylesheet]').href).host"))!;
                if (firstHost is null) firstHost = host;
                else Require(host != firstHost, "Themes with identical CSS reused another theme's asset origin.");
                await content.ProbeWebView.ExecuteScriptAsync("""
                    (() => {
                        document.getElementById('theme-asset-probe')?.remove();
                        const image = document.createElement('img'); image.id = 'theme-asset-probe';
                        image.src = new URL('asset.svg', document.querySelector('link[rel=stylesheet]').href).href;
                        document.body.append(image);
                    })()
                    """);
                await WaitFor(() => ScriptBool(content, $"document.getElementById('theme-asset-probe').naturalWidth === {width}"),
                    name + " isolated local asset");
            }
            FileUtilities.WriteAllTextAtomically(Path.Combine(customFolders[1], "Info.json"), """{"grayscaleIcon":true}""");
            manager.Rescan();
            await WaitFor(() => ScriptBool(content, "getComputedStyle(document.getElementById('nnwImageIcon')).filter.includes('grayscale(1)')"),
                "Live metadata-only theme update");
            FileUtilities.WriteAllTextAtomically(Path.Combine(customFolders[1], "stylesheet.css"), ".articleBody { color: rgb(71,81,91); }");
            manager.Rescan();
            await WaitFor(async () => content.ProbeArticleReady
                && await ScriptString(content, "getComputedStyle(document.getElementById('theme-body-marker')).color") == "rgb(71, 81, 91)"
                && await ScriptString(companion.ProbeContent, "getComputedStyle(document.getElementById('theme-body-marker')).color") == "rgb(71, 81, 91)",
                "Live CSS updates both open readers");
            Directory.Delete(customFolders[1], recursive: true);
            manager.Rescan();
            await WaitFor(() => Task.FromResult(manager.CurrentTheme.Name == "Default"
                && (combo.SelectedItem as ComboBoxItem)?.Tag as string == "Default"
                && !combo.Items.OfType<ComboBoxItem>().Any(item => (string)item.Tag == "Theme Asset B")),
                "Deleting the selected theme restores Default and updates existing controls");
            File.WriteAllText(Path.Combine(OutputDirectory, "themes.json"), JsonSerializer.Serialize(samples,
                new JsonSerializerOptions { WriteIndented = true }));
            return new { BundledThemes = samples.Count, NativeThemeMenu = true, Preferences = true, MultiWindow = true,
                FiveTextSizes = true, DarkAppearance = true, EmbeddedFonts = true, LocalFonts = true, IsolatedAssets = true,
                LiveCssAndMetadata = true, RemovalFallback = true };
        }
        catch
        {
            File.WriteAllText(Path.Combine(OutputDirectory, "theme-failure.json"),
                await content.ProbeWebView.ExecuteScriptAsync("({html:document.body.innerHTML,processed:window.__wnwArticleProcessed,"
                    + "mode:document.compatMode,style:Array.from(document.styleSheets).map(sheet=>sheet.href),"
                    + "fonts:Array.from(document.fonts).map(font=>({family:font.family,status:font.status}))})"));
            throw;
        }
        finally
        {
            themeMenu?.Hide();
            preferences?.Close();
            if (companion is not null) await CloseWindowAsync(companion);
            manager.SelectByName(originalTheme);
            foreach (var folder in customFolders) if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            manager.Rescan();
            defaults.ArticleTextSizeRaw = originalSize;
            defaults.AppearanceMode = originalAppearance;
            defaults.ArticleContentJavascriptEnabled = originalScripts;
            defaults.ShowStatusBar = originalStatus;
            defaults.ShowStatusBarThemeMenu = originalStatusThemes;
            account.RemoveFeed(feed);
            model.BuildSidebar();
            await content.RestoreStateAsync(originalState);
        }
    }

    private static async Task<object?> CheckThemeImportConfirmationAsync(MainWindow window)
    {
        var manager = ArticleThemesManager.Shared;
        string previous = manager.CurrentTheme.Name;
        string archive = Path.Combine(OutputDirectory, "theme-import.zip");
        string target = Path.Combine(manager.FolderPath, "Native Import.nnwtheme");
        ContentDialog? Dialog(MainWindow? owner = null) => VisualTreeHelper.GetOpenPopupsForXamlRoot((owner ?? window).ProbeContent.XamlRoot)
            .SelectMany(popup => Descendants(popup.Child)).OfType<ContentDialog>().FirstOrDefault();
        Task Start(MainWindow? owner = null) => (Task)(typeof(MainWindow).GetMethod("InstallThemeAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(owner ?? window, [archive, null]) ?? throw new InvalidOperationException("The native theme importer did not start."));
        void WriteArchive(string css, int version)
        {
            if (File.Exists(archive)) File.Delete(archive);
            using var zip = ZipFile.Open(archive, ZipArchiveMode.Create);
            using (var writer = new StreamWriter(zip.CreateEntry("Native Import.nnwtheme/Info.json").Open()))
                writer.Write(JsonSerializer.Serialize(new { creatorName = "Fixture Author", creatorHomePage = "https://example.com", version }));
            using var style = new StreamWriter(zip.CreateEntry("Native Import.nnwtheme/stylesheet.css").Open());
            style.Write(css);
        }
        Task? operation = null;
        MainWindow? closingWindow = null;
        try
        {
            WriteArchive("body { color: rgb(1,2,3); }", 1);
            operation = Start();
            await WaitFor(() => Task.FromResult(Dialog()?.IsLoaded == true || operation.IsCompleted), "Theme metadata confirmation");
            var dialog = Dialog();
            Require(dialog is not null && !operation.IsCompleted && dialog.Title.ToString()!.Contains("Fixture Author", StringComparison.Ordinal)
                && !Directory.Exists(target), "Theme installation ran before presenting metadata for confirmation.");
            InvokeDialogButton(dialog!, "CloseButton");
            await operation;
            Require(!Directory.Exists(target) && manager.CurrentTheme.Name == previous, "Cancelling an import changed themes or selection.");

            operation = Start();
            await WaitFor(() => Task.FromResult(Dialog()?.IsLoaded == true), "Accepted theme metadata");
            WriteArchive("body { color: rgb(9,8,7); }", 99);
            InvokeDialogButton(Dialog()!, "PrimaryButton");
            await operation;
            Require(File.ReadAllText(Path.Combine(target, "stylesheet.css")) == "body { color: rgb(1,2,3); }",
                "The source archive changed the theme after its metadata had been approved.");
            string original = File.ReadAllText(Path.Combine(target, "stylesheet.css"));
            WriteArchive("body { color: rgb(4,5,6); }", 2);
            operation = Start();
            await WaitFor(() => Task.FromResult(Dialog()?.IsLoaded == true), "Replacement theme metadata");
            InvokeDialogButton(Dialog()!, "PrimaryButton");
            await WaitFor(() => Task.FromResult(Dialog() is { IsLoaded: true } replacement
                && replacement.Title as string == AppStrings.Get("ThemeReplaceTitle")
                && Descendants(replacement).OfType<Button>().Any(button => button.Name == "CloseButton" && button.IsLoaded)),
                "Overwrite confirmation");
            Require(File.ReadAllText(Path.Combine(target, "stylesheet.css")) == original, "Replacement modified the existing theme before confirmation.");
            InvokeDialogButton(Dialog()!, "CloseButton");
            await operation;
            Require(File.ReadAllText(Path.Combine(target, "stylesheet.css")) == original, "Cancelling replacement changed the installed theme.");

            operation = Start();
            await WaitFor(() => Task.FromResult(Dialog()?.IsLoaded == true), "Confirmed replacement metadata");
            InvokeDialogButton(Dialog()!, "PrimaryButton");
            await WaitFor(() => Task.FromResult(Dialog() is { IsLoaded: true } replacement
                && replacement.Title as string == AppStrings.Get("ThemeReplaceTitle")
                && Descendants(replacement).OfType<Button>().Any(button => button.Name == "PrimaryButton" && button.IsLoaded)),
                "Confirmed overwrite dialog");
            InvokeDialogButton(Dialog()!, "PrimaryButton");
            await operation;
            Require(File.ReadAllText(Path.Combine(target, "stylesheet.css")) == "body { color: rgb(4,5,6); }"
                && manager.CurrentTheme.Name == "Native Import", "Confirmed replacement was not installed and selected.");
            closingWindow = App.OpenMainWindow();
            await closingWindow.ContentInitialization;
            await closingWindow.Restoration;
            operation = Start(closingWindow);
            await WaitFor(() => Task.FromResult(Dialog(closingWindow)?.IsLoaded == true), "Theme import before window shutdown");
            await CloseWindowAsync(closingWindow);
            closingWindow = null;
            await operation;
            Require(File.ReadAllText(Path.Combine(target, "stylesheet.css")) == "body { color: rgb(4,5,6); }",
                "Closing a confirmation window modified an installed theme.");
            Require(!Directory.EnumerateDirectories(manager.FolderPath, ".tmp-*").Any(), "Theme import left its staging files behind.");
            return new { MetadataBeforeInstall = true, CancellationPreservesSelection = true, ImmutablePreview = true,
                OverwriteConfirmation = true, AtomicReplacement = true, StagingCleanup = true, ClosingCancelsImport = true };
        }
        finally
        {
            Dialog()?.Hide();
            if (closingWindow is not null)
            {
                Dialog(closingWindow)?.Hide();
                await CloseWindowAsync(closingWindow);
            }
            if (operation is not null) await operation;
            manager.SelectByName(previous);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            manager.Rescan();
        }
    }
}
#endif
