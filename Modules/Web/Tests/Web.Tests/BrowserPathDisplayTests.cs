using Xunit;

namespace WinNewsWire.Web.Tests;

public sealed class BrowserPathDisplayTests
{
    [Fact]
    public void RootApplications() => Assert.Equal("/Applications", BrowserPathDisplay.ForCanonicalParentPath("/Applications"));

    [Fact]
    public void HomePathShownAsAbsolute() => Assert.Equal("/Users/brent/Applications", BrowserPathDisplay.ForCanonicalParentPath("/Users/brent/Applications"));

    [Fact]
    public void RootPathOutsideHomeIsLeftAbsolute()
        => Assert.Equal("/Library/Internet Plug-Ins", BrowserPathDisplay.ForCanonicalParentPath("/Library/Internet Plug-Ins"));

    [Fact]
    public void DeepRootPathIsTruncated()
        => Assert.Equal("/some/\u2026/structure", BrowserPathDisplay.ForCanonicalParentPath("/some/deep/random/path/structure"));

    [Fact]
    public void VolumeApplications()
        => Assert.Equal("/Macintosh HD/Applications", BrowserPathDisplay.ForCanonicalParentPath("/Volumes/Macintosh HD/Applications"));

    [Fact]
    public void VolumeUserApplications()
        => Assert.Equal("/Macintosh HD/Users/brent/Applications", BrowserPathDisplay.ForCanonicalParentPath("/Volumes/Macintosh HD/Users/brent/Applications"));

    [Fact]
    public void VolumeRoot() => Assert.Equal("/MyDrive", BrowserPathDisplay.ForCanonicalParentPath("/Volumes/MyDrive"));

    [Fact]
    public void VolumeDeepPathIsTruncated()
        => Assert.Equal("/MyDrive/\u2026/D", BrowserPathDisplay.ForCanonicalParentPath("/Volumes/MyDrive/A/B/C/D"));

    [Fact]
    public void VolumeNameWithSpacesPreserved()
        => Assert.Equal("/External Drive/Apps", BrowserPathDisplay.ForCanonicalParentPath("/Volumes/External Drive/Apps"));

    [Theory]
    [InlineData(@"C:\Program Files\Browser", @"C:\Program Files\Browser")]
    [InlineData(@"C:\Users\Reader\Apps", @"C:\Users\Reader\Apps")]
    [InlineData(@"D:\Applications\Channel\Build\Browser", "D:\\Applications\\\u2026\\Browser")]
    [InlineData(@"\\server\share\A\B\C\Browser", "\\\\server\\share\\\u2026\\Browser")]
    public void WindowsPathsRetainDriveOrShare(string path, string expected)
        => Assert.Equal(expected, BrowserPathDisplay.ForCanonicalParentPath(path));
}
