namespace WinNewsWire.AppShared;

/// <summary>
/// Port of NetNewsWire's <c>HelpURL</c>. Centralizes the help/support URLs
/// the menu bar, About box, and right-click menus link out to. WinNewsWire
/// reuses NetNewsWire's hosted help pages because the Windows port shares the
/// same feed-reader concepts; later we can override individual URLs when a
/// Windows-specific help page exists.
/// </summary>
/// <remarks>
/// This module targets <c>net8.0</c> (no WinUI dependency) so the URLs are
/// exposed as plain string constants. The WinUI layer launches them via
/// <c>Windows.System.Launcher</c>; non-WinUI consumers (tests, console
/// callers) can read the strings directly.
/// </remarks>
public static class HelpUrl
{
    public const string HelpHome      = "https://netnewswire.com/help/";
    public const string Website       = "https://netnewswire.com/";
    public const string ReleaseNotes  = "https://github.com/Ranchero-Software/NetNewsWire/releases/";
    public const string HowToSupport  = "https://github.com/Ranchero-Software/NetNewsWire/blob/main/Technotes/HowToSupportNetNewsWire.markdown";
    public const string GithubRepo    = "https://github.com/Ranchero-Software/NetNewsWire";
    public const string BugTracker    = "https://github.com/Ranchero-Software/NetNewsWire/issues";
    public const string Discourse     = "https://discourse.netnewswire.com/";
    public const string Technotes     = "https://github.com/Ranchero-Software/NetNewsWire/tree/main/Technotes";
    public const string PrivacyPolicy = "https://netnewswire.com/privacypolicy.html";
}

