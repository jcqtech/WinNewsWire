using WinNewsWire.Parsers;

namespace WinNewsWire.Account;

/// <summary>
/// Port of NetNewsWire's <c>OPMLFile</c>. Wraps a <c>Subscriptions.opml</c>
/// file living next to an account on disk; supports batch import from disk
/// and coalesced autosave when the account's feed/folder set changes.
/// </summary>
/// <remarks>
/// <para>
/// On macOS this class also auto-loads the file at the next launch (so an
/// out-of-band edit propagates into the running app). On Windows we keep
/// <c>Settings.json</c> as the authoritative runtime state and treat the
/// <c>.opml</c> as an import/export-compatible mirror: it's regenerated on
/// every structural change so the user can hand the file to other tools.
/// </para>
/// <para>
/// Autosaves coalesce inside the active <see cref="Account.PerformBatchUpdate"/>
/// scope — a single Import → re-export round trip writes the file once,
/// matching the Mac app's <c>CoalescingQueue(interval: 0.5)</c> behaviour.
/// </para>
/// </remarks>
public sealed class OpmlFile
{
    private readonly Account _account;
    public string FilePath { get; }

    public OpmlFile(string filePath, Account account)
    {
        FilePath = filePath;
        _account = account;
        _account.AccountStructureChanged += OnAccountStructureChanged;
    }

    /// <summary>
    /// Parses <see cref="FilePath"/> (if it exists) and applies the contained
    /// outlines to the associated account inside a single
    /// <see cref="Account.PerformBatchUpdate(Action)"/> scope, so subscribers
    /// see one structural change instead of one per item.
    /// </summary>
    public void Load()
    {
        if (!File.Exists(FilePath)) return;
        byte[] bytes;
        try { bytes = File.ReadAllBytes(FilePath); }
        catch { return; }

        OpmlDocument doc;
        try
        {
            doc = OpmlParser.Parse(new ParserData(FilePath, bytes));
        }
        catch
        {
            // Don't crash if the user hand-edited the file into invalid XML.
            return;
        }

        // Account.LoadOpmlItems normalizes and batches internally.
        _account.LoadOpmlItems(doc.Children);
    }

    /// <summary>Persists the account's current feed/folder structure to
    /// <see cref="FilePath"/> as an OPML 2.0 document.</summary>
    public void Save()
    {
        try
        {
            var opml = _account.ExportOpml();
            // Atomic write: temp + replace so a crash mid-write doesn't leave
            // a half-written file the next launch will refuse to parse.
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, opml);
            if (File.Exists(FilePath))
                File.Replace(temp, FilePath, destinationBackupFileName: null);
            else
                File.Move(temp, FilePath);
        }
        catch
        {
            // Swallow OPML write errors — the authoritative state still lives
            // in Settings.json, and the next save attempt will retry.
        }
    }

    private void OnAccountStructureChanged(object? sender, EventArgs e) => Save();
}
