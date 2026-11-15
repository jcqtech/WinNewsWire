using System.IO.Compression;
using System.Text;
using WinNewsWire.AppShared.ArticleThemes;
using WinNewsWire.Core;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

public sealed class ThemeUnzipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WinNewsWire.ThemeTests." + Guid.NewGuid().ToString("N"));
    public ThemeUnzipTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void NormalThemeArchiveExtracts()
    {
        var archive = WriteZip(BenignThemeZip);
        var theme = ArticleThemeDownloader.UnzipTheme(archive, Path.Combine(_root, "extract"));
        Assert.Equal("Good Theme.nnwtheme", Path.GetFileName(theme));
        Assert.True(File.Exists(Path.Combine(theme, "Info.plist")));
    }

    [Fact]
    public void PathTraversalArchiveIsRejectedAndWritesNothingOutsideDestination()
    {
        var archive = WriteZip(PathTraversalZip);
        Assert.ThrowsAny<Exception>(() => ArticleThemeDownloader.UnzipTheme(archive, Path.Combine(_root, "extract")));
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
    }

    [Fact]
    public void ThemeMetadataCannotChooseAnExternalInstallationPath()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "keep.txt");
        File.WriteAllText(sentinel, "keep");
        var archive = Path.Combine(_root, "metadata.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("Good.nnwtheme/Info.json").Open());
            writer.Write(System.Text.Json.JsonSerializer.Serialize(new { Name = outside, CreatorName = "Creator", Version = 3 }));
        }
        using var manager = Manager();
        var installed = manager.InstallFromZip(archive);
        Assert.Equal(Path.Combine(manager.FolderPath, "Good.nnwtheme"), installed.Path);
        Assert.Equal("Good", installed.Name);
        Assert.Equal("keep", File.ReadAllText(sentinel));
    }

    [Fact]
    public void InvalidReplacementLeavesTheInstalledThemeUntouched()
    {
        using var manager = Manager();
        var installed = manager.InstallFromZip(WriteZip(BenignThemeZip));
        var templatePath = Path.Combine(installed.Path, "template.html");
        var template = File.ReadAllText(templatePath);
        Assert.ThrowsAny<Exception>(() => manager.InstallFromZip(WriteZip(PathTraversalZip)));
        Assert.Equal(template, File.ReadAllText(templatePath));
    }

    [Fact]
    public void PreparedThemeDoesNotInstallUntilConfirmedAndUsesItsInspectedSnapshot()
    {
        using var manager = Manager();
        string archive = WriteZip(BenignThemeZip);
        using var prepared = manager.PrepareImport(archive);
        string target = Path.Combine(manager.FolderPath, "Good Theme.nnwtheme");
        Assert.False(Directory.Exists(target));
        Assert.Equal("Good Theme", prepared.Theme.Name);
        File.WriteAllBytes(archive, Convert.FromBase64String(PathTraversalZip));
        var installed = prepared.Install();
        Assert.Equal(target, installed.Path);
        Assert.True(File.Exists(Path.Combine(target, "template.html")));
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
    }

    [Fact]
    public void CancelledPreparationPreservesExistingThemeAndRemovesStaging()
    {
        using var manager = Manager();
        var installed = manager.InstallFromZip(WriteZip(BenignThemeZip));
        string original = File.ReadAllText(Path.Combine(installed.Path, "stylesheet.css"));
        using (var prepared = manager.PrepareImport(WriteZip(BenignThemeZip)))
            Assert.True(prepared.ReplacesExisting);
        Assert.Equal(original, File.ReadAllText(Path.Combine(installed.Path, "stylesheet.css")));
        Assert.Empty(Directory.EnumerateDirectories(manager.FolderPath, ".tmp-*"));
    }

    [Fact]
    public void ThemeAppearingAfterInspectionCannotBeOverwrittenWithoutConfirmation()
    {
        using var manager = Manager();
        using var prepared = manager.PrepareImport(WriteZip(BenignThemeZip));
        var installed = manager.InstallFromZip(WriteZip(BenignThemeZip));
        File.WriteAllText(Path.Combine(installed.Path, "stylesheet.css"), "body { color: red; }");
        Assert.False(prepared.ReplacesExisting);
        Assert.Throws<IOException>(() => prepared.Install());
        Assert.Equal("body { color: red; }", File.ReadAllText(Path.Combine(installed.Path, "stylesheet.css")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ExpandedArchiveLimitIsEnforcedBeforeExtraction(int excess)
    {
        var archive = Path.Combine(_root, "size.zip");
        const string info = "<plist><dict><key>name</key><string>Good</string></dict></plist>";
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("Good.nnwtheme/Info.plist").Open(), new UTF8Encoding(false))) writer.Write(info);
            using var content = zip.CreateEntry("Good.nnwtheme/stylesheet.css").Open();
            content.Write(new byte[checked((int)ArticleThemeDownloader.MaximumThemeSize - Encoding.UTF8.GetByteCount(info) + excess)]);
        }
        var destination = Path.Combine(_root, "extract");
        if (excess == 0)
            Assert.Equal("Good.nnwtheme", Path.GetFileName(ArticleThemeDownloader.UnzipTheme(archive, destination)));
        else
        {
            Assert.Throws<InvalidDataException>(() => ArticleThemeDownloader.UnzipTheme(archive, destination));
            Assert.False(Directory.Exists(destination));
        }
    }

    [Fact]
    public void NativeMetadataAndStylesheetOnlyThemesAreSupported()
    {
        var themePath = Path.Combine(_root, "Folder Name.nnwtheme");
        Directory.CreateDirectory(themePath);
        File.WriteAllText(Path.Combine(themePath, "Info.plist"), """
            <plist><dict><key>name</key><string>Metadata name</string><key>themeIdentifier</key><string>test.theme</string>
            <key>creatorName</key><string>Creator</string><key>creatorHomePage</key><string>https://example.com</string>
            <key>version</key><integer>3</integer></dict></plist>
            """);
        File.WriteAllText(Path.Combine(themePath, "stylesheet.css"), "body { color: red; }");
        var theme = Assert.IsType<ArticleTheme>(ArticleTheme.LoadFromFolder(themePath, false));
        Assert.Equal("Folder Name", theme.Name);
        Assert.Equal("test.theme", theme.ThemeIdentifier);
        Assert.Equal("Creator", theme.CreatorName);
        Assert.Equal("3", theme.Version);
        Assert.Contains("color: red", theme.StylesheetCss);
        Assert.NotEmpty(theme.TemplateHtml);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void JsonThemeMetadataControlsArticleIconGrayscale(bool enabled)
    {
        var path = Path.Combine(_root, "Grayscale.nnwtheme");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "Info.json"),
            System.Text.Json.JsonSerializer.Serialize(new { Name = "Grayscale", grayscaleIcon = enabled }));
        var theme = Assert.IsType<ArticleTheme>(ArticleTheme.LoadFromFolder(path, false));
        Assert.Equal(enabled, theme.UsesGrayscaleIcon);
    }

    [Fact]
    public void MalformedGrayscaleMetadataRejectsTheTheme()
    {
        var path = Path.Combine(_root, "Invalid.nnwtheme");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "Info.json"), """{"grayscaleIcon":42}""");
        Assert.Null(ArticleTheme.LoadFromFolder(path, false));
    }

    [Fact]
    public void ThemeNamesAreSortedUniqueAndPreferTheBundledCopy()
    {
        using var manager = Manager();
        foreach (string name in new[] { "zeta", "alpha", "Gray" })
        {
            string folder = Path.Combine(manager.FolderPath, name + ".nnwtheme");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Info.json"), "{}");
        }
        manager.Rescan();
        var themes = manager.Themes;
        Assert.Equal("Default", themes[0].Name);
        Assert.Equal(themes.Skip(1).Select(theme => theme.Name).Order(StringComparer.CurrentCultureIgnoreCase),
            themes.Skip(1).Select(theme => theme.Name));
        Assert.True(Assert.Single(themes, theme => theme.Name == "Gray").IsAppTheme);
        manager.SelectByName("Gray");
        Assert.True(manager.CurrentTheme.IsAppTheme);
    }

    [Fact]
    public void MetadataOnlyChangesRefreshTheCurrentTheme()
    {
        using var manager = Manager();
        string folder = Path.Combine(manager.FolderPath, "Edited.nnwtheme");
        Directory.CreateDirectory(folder);
        string metadata = Path.Combine(folder, "Info.json");
        File.WriteAllText(metadata, """{"grayscaleIcon":false}""");
        manager.Rescan();
        manager.SelectByName("Edited");
        int notifications = 0;
        manager.CurrentThemeChanged += (_, _) => Interlocked.Increment(ref notifications);
        FileUtilities.WriteAllTextAtomically(metadata, """{"grayscaleIcon":true}""");
        manager.Rescan();
        Assert.True(manager.CurrentTheme.UsesGrayscaleIcon);
        Assert.True(Volatile.Read(ref notifications) > 0);
    }

    [Fact]
    public void ThemeRemovalRestoresAndPersistsDefault()
    {
        using var manager = Manager();
        string folder = Path.Combine(manager.FolderPath, "Removed.nnwtheme");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Info.json"), "{}");
        manager.Rescan();
        manager.SelectByName("Removed");
        Directory.Delete(folder, recursive: true);
        manager.Rescan();
        Assert.Equal("Default", manager.CurrentTheme.Name);
        using var reopened = Manager();
        Assert.Equal("Default", reopened.CurrentTheme.Name);
    }

    [Theory]
    [InlineData(".. /escaped.txt")]
    [InlineData("Good.nnwtheme/CON.txt")]
    [InlineData("Good.nnwtheme/aux")]
    [InlineData("Good.nnwtheme/LPT1.png")]
    [InlineData("Good.nnwtheme/trailing.")]
    [InlineData("Good.nnwtheme/trailing ")]
    public void WindowsFilenameAliasesAreRejectedBeforeExtraction(string entryName)
    {
        var archive = Path.Combine(_root, "unsafe.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry(entryName).Open());
            writer.Write("unsafe");
        }
        var destination = Path.Combine(_root, "extract");
        Assert.Throws<InvalidDataException>(() => ArticleThemeDownloader.UnzipTheme(archive, destination));
        Assert.False(Directory.Exists(destination));
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
    }

    private ArticleThemesManager Manager()
        => new(Path.Combine(_root, "Themes"), new AppDefaults(Path.Combine(_root, "defaults.json")));
    private string WriteZip(string content)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        File.WriteAllBytes(path, Convert.FromBase64String(content));
        return path;
    }

    private const string BenignThemeZip = """
        UEsDBBQAAAAAAGejH10lf2Ft4wAAAOMAAAAeAAAAR29vZCBUaGVtZS5ubnd0aGVtZS9JbmZvLnBsaXN0PD94bWwgdmVyc2lvbj0i
        MS4wIiBlbmNvZGluZz0iVVRGLTgiPz4KPCFET0NUWVBFIHBsaXN0IFBVQkxJQyAiLS8vQXBwbGUvL0RURCBQTElTVCAxLjAvL0VO
        IiAiaHR0cDovL3d3dy5hcHBsZS5jb20vRFREcy9Qcm9wZXJ0eUxpc3QtMS4wLmR0ZCI+CjxwbGlzdCB2ZXJzaW9uPSIxLjAiPjxk
        aWN0PjxrZXk+bmFtZTwva2V5PjxzdHJpbmc+R29vZCBUaGVtZTwvc3RyaW5nPjwvZGljdD48L3BsaXN0PgpQSwMEFAAAAAAAZ6Mf
        XVokqGUXAAAAFwAAACIAAABHb29kIFRoZW1lLm5ud3RoZW1lL3N0eWxlc2hlZXQuY3NzYm9keSB7IGNvbG9yOiBibGFjazsgfQpQ
        SwMEFAAAAAAAZ6MfXZN1YIIjAAAAIwAAACEAAABHb29kIFRoZW1lLm5ud3RoZW1lL3RlbXBsYXRlLmh0bWw8aHRtbD48Ym9keT5b
        W2JvZHldXTwvYm9keT48L2h0bWw+ClBLAQIUAxQAAAAAAGejH10lf2Ft4wAAAOMAAAAeAAAAAAAAAAAAAACAAQAAAABHb29kIFRo
        ZW1lLm5ud3RoZW1lL0luZm8ucGxpc3RQSwECFAMUAAAAAABnox9dWiSoZRcAAAAXAAAAIgAAAAAAAAAAAAAAgAEfAQAAR29vZCBU
        aGVtZS5ubnd0aGVtZS9zdHlsZXNoZWV0LmNzc1BLAQIUAxQAAAAAAGejH12TdWCCIwAAACMAAAAhAAAAAAAAAAAAAACAAXYBAABH
        b29kIFRoZW1lLm5ud3RoZW1lL3RlbXBsYXRlLmh0bWxQSwUGAAAAAAMAAwDrAAAA2AEAAAAA
        """;
    private const string PathTraversalZip = """
        UEsDBBQAAAAAAA2mH10lf2Ft4wAAAOMAAAAeAAAAR29vZCBUaGVtZS5ubnd0aGVtZS9JbmZvLnBsaXN0PD94bWwgdmVyc2lvbj0i
        MS4wIiBlbmNvZGluZz0iVVRGLTgiPz4KPCFET0NUWVBFIHBsaXN0IFBVQkxJQyAiLS8vQXBwbGUvL0RURCBQTElTVCAxLjAvL0VO
        IiAiaHR0cDovL3d3dy5hcHBsZS5jb20vRFREcy9Qcm9wZXJ0eUxpc3QtMS4wLmR0ZCI+CjxwbGlzdCB2ZXJzaW9uPSIxLjAiPjxk
        aWN0PjxrZXk+bmFtZTwva2V5PjxzdHJpbmc+R29vZCBUaGVtZTwvc3RyaW5nPjwvZGljdD48L3BsaXN0PgpQSwMEFAAAAAAADaYf
        XVsM+JIHAAAABwAAAA4AAAAuLi9lc2NhcGVkLnR4dGVzY2FwZWRQSwECFAMUAAAAAAANph9dJX9hbeMAAADjAAAAHgAAAAAAAAAA
        AAAAgAEAAAAAR29vZCBUaGVtZS5ubnd0aGVtZS9JbmZvLnBsaXN0UEsBAhQDFAAAAAAADaYfXVsM+JIHAAAABwAAAA4AAAAAAAAA
        AAAAAIABHwEAAC4uL2VzY2FwZWQudHh0UEsFBgAAAAACAAIAiAAAAFIBAAAAAA==
        """;

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
