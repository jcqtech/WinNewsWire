using System.Runtime.Versioning;
using Xunit;

namespace WinNewsWire.Core.Tests;

public sealed class WindowsCommandLineTests
{
    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void QuotedPathsAndFeedArgumentsRemainSeparate()
    {
        var arguments = WindowsCommandLine.Split(
            "\"C:\\Apps\\Reader App.exe\" --subscribe \"feeds://example.test/feed?a=1&b=2\" \"C:\\Feeds\\My subscriptions.opml\"");
        Assert.Equal([
            @"C:\Apps\Reader App.exe", "--subscribe", "feeds://example.test/feed?a=1&b=2", @"C:\Feeds\My subscriptions.opml"
        ], arguments);
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void EmptyCommandLineDoesNotInventAnExecutable()
        => Assert.Empty(WindowsCommandLine.Split(""));

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void EmptyQuotedArgumentIsPreserved()
        => Assert.Equal(["reader.exe", ""], WindowsCommandLine.Split("reader.exe \"\""));

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Requires the Windows command-line parser.";
        }
    }
}
