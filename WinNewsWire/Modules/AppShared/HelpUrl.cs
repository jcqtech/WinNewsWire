namespace WinNewsWire.AppShared;

/// <summary>
/// Centralizes the help/support URLs the menu bar, About box, and right-click
/// menus link out to. Originally a 1:1 port of NetNewsWire's <c>HelpURL</c>
/// constants, but the WinNewsWire pages are now the canonical destination so
/// users land on Windows-specific docs and the WinNewsWire GitHub repo
/// (jcqtech/WinNewsWire) rather than the upstream Mac project.
/// </summary>
/// <remarks>
/// This module targets <c>net8.0</c> (no WinUI dependency) so the URLs are
/// exposed as plain string constants. The WinUI layer launches them via
/// <c>Windows.System.Launcher</c>; non-WinUI consumers (tests, console
/// callers) can read the strings directly.
/// </remarks>
public static class HelpUrl
{
    public const string HelpHome      = "https://winnewswire.com/help/";
    public const string Website       = "https://winnewswire.com/";
    public const string ReleaseNotes  = "https://winnewswire.com/releasenotes/";
    public const string HowToSupport  = "https://winnewswire.com/support/";
    public const string GithubRepo    = "https://github.com/jcqtech/WinNewsWire";
    public const string BugTracker    = "https://github.com/jcqtech/WinNewsWire/issues";
    public const string Discourse     = "https://winnewswire.com/community/";
    public const string Technotes     = "https://winnewswire.com/docs/";
    public const string PrivacyPolicy = "https://winnewswire.com/privacy/";
}


